using PdfNetStack.Objects;

namespace PdfNetStack.Documents;

/// <content>
/// The logical structure (ISO 32000-1, 14.7) travelling with imported pages, so a merged or split
/// tagged PDF stays tagged. The marked content of a page keeps its MCIDs, which number sequences
/// within one content stream; what changes is where the structure tree and its parent tree live.
/// </content>
internal sealed partial class PageImporter
{
    // Keys by which XObjects find their place in the parent tree (ISO 32000-1, 14.7.4.4).
    private static readonly string[] XObjectStructureKeys = ["StructParents", "StructParent"];

    // Mark information flags (ISO 32000-1, Table 321) a target keeps once any source sets them.
    private static readonly string[] MarkInfoFlags = ["Marked", "Suspects", "UserProperties"];

    private StructureIndex? _sourceStructure;
    private bool _sourceStructureRead;

    // The structure being copied by the current Import call; null outside it.
    private StructureCopy? _structure;

    // Custom structure types and attribute classes of the source renamed because the target
    // already uses the name for something else. Fixed once the source's maps are merged.
    private Dictionary<string, string>? _roleRenames;
    private Dictionary<string, string>? _classRenames;
    private bool _roleMapMerged;
    private bool _classMapMerged;

    // Source numbers of shared XObjects whose structure key was removed because no element
    // of the import that copied them tags them. A later import that does gives the key back.
    private readonly HashSet<int> _unkeyed = [];

    // Element IDs of the source that another element of the target already uses, with the
    // unique ID their copies carry. Table header references are rewritten to match.
    private readonly Dictionary<string, string> _idRenames = new(StringComparer.Ordinal);

    /// <summary>
    /// The source's structure tree, read on first use and reused by every later import from
    /// this importer, or for a document copying its own pages, by the rest of the call. Null when
    /// the source is not tagged.
    /// </summary>
    internal StructureIndex? SourceStructure(CancellationToken ct)
    {
        if (!_sourceStructureRead)
        {
            _sourceStructure = StructureIndex.Read(_source, ct);
            _sourceStructureRead = true;
        }

        return _sourceStructure;
    }

    /// <summary>
    /// Uses a reading of the source's structure tree made by another importer of the same source,
    /// so splitting a tagged document into many parts reads its tree once, not once per part.
    /// </summary>
    internal void UseSourceStructure(StructureIndex? index)
    {
        _sourceStructure = index;
        _sourceStructureRead = true;
    }

    /// <summary>
    /// What finishing the structure may change in objects the target already has: the catalog,
    /// the structure root with its kids and parent tree, the role and class maps while this
    /// source's are not merged in, and XObjects an earlier import left without their key.
    /// </summary>
    private TargetState SaveStructureState()
    {
        var state = new TargetState();
        state.Save(_target.Catalog);
        if (_target.Resolve(_target.Catalog["StructTreeRoot"]) is PdfDictionary root)
        {
            state.Save(root);
            state.Save(_target.Resolve(root["K"]) as PdfArray);
            if (_target.Resolve(root["ParentTree"]) is PdfDictionary tree)
            {
                state.Save(tree);
                state.Save(_target.Resolve(tree["Nums"]) as PdfArray);
                state.Save(_target.Resolve(tree["Kids"]) as PdfArray);
            }

            if (!_roleMapMerged) state.Save(_target.Resolve(root["RoleMap"]) as PdfDictionary);
            if (!_classMapMerged) state.Save(_target.Resolve(root["ClassMap"]) as PdfDictionary);
        }

        foreach (int source in _unkeyed)
        {
            if (_shared.TryGetValue(source, out int number) && _target.Objects.TryGetValue(number, out PdfObject? copy) && copy is PdfStream stream) state.Save(stream.Dictionary);
        }

        return state;
    }

    /// <summary>
    /// Copies the structure elements that tag content of the pages this round copies, given in
    /// <paramref name="pages"/>, with every ancestor up to the root, so the copies form a tree of
    /// their own. Content on other pages, and elements left with no content, are not copied. Runs
    /// before the work queue drains, so objects the elements refer to (attributes, form XObjects
    /// of marked content references) are copied with everything else, with references to pages
    /// following <paramref name="pageMap"/>. Returns null when the source has no structure tree.
    /// </summary>
    private StructureCopy? ImportStructure(
        Dictionary<int, int> pages, Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work, CancellationToken ct)
    {
        StructureIndex? index = SourceStructure(ct);
        if (index is null) return null;

        var copy = new StructureCopy(index, index.Ancestry(pages.Keys), pages);
        _structure = copy;
        if (!SameDocument)
        {
            _roleRenames ??= Renames(index.Root, "RoleMap", sameValueIsNoConflict: true);
            _classRenames ??= Renames(index.Root, "ClassMap", sameValueIsNoConflict: false);
            if (index.HasIds) copy.TargetIds = TargetIds();
        }

        foreach (PdfObject kid in index.RootKids)
        {
            if (CopyElement(kid, null, 0, copy, pageMap, batch, work, ct) is { } top) copy.Tops.Add(top);
        }

        ResolveReferences(copy, ct);

        // Attribute classes travel with the elements that use them; their values may refer to
        // other objects, which the work queue copies.
        if (copy.Tops.Count > 0 && !SameDocument && !_classMapMerged && _source.Resolve(index.Root["ClassMap"]) is PdfDictionary classes)
        {
            copy.ClassMap = new PdfDictionary(classes.Count);
            for (int i = 0; i < classes.Count; i++)
            {
                (string name, PdfObject value) = classes.EntryAt(i);
                copy.ClassMap.Set(Renamed(_classRenames, name), CopyValue(value, null, 1, pageMap, batch, work));
            }
        }

        return copy;
    }

    /// <summary>
    /// The copy of one structure element and the part of its subtree that tags imported content,
    /// or null when none of it does. <paramref name="inheritedPage"/> is the nearest ancestor's /Pg,
    /// which an element without its own passes on to its MCIDs (ISO 32000-1, Table 323).
    /// </summary>
    private (PdfDictionary Dictionary, int Number)? CopyElement(
        PdfObject entry, PdfReference? inheritedPage, int depth, StructureCopy copy, Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work, CancellationToken ct)
    {
        if (depth > StructureIndex.MaxDepth || copy.Spent) return null;
        int? number = entry is PdfReference r ? r.Number : null;

        // An element reached twice, through a cycle or a second parent, is copied once.
        if (number is int n && (!copy.Kept.Contains(n) || !copy.Visited.Add(n))) return null;
        if (_source.Resolve(entry) is not PdfDictionary element || StructureIndex.IsContentReference(element)) return null;
        if (++copy.Copied % CancellationInterval == 0) ct.ThrowIfCancellationRequested();

        // An element written in place can be reached again through a /K array that contains it.
        if (number is null && !copy.DirectVisited.Add(element)) return null;

        // The index read a shared /K array for one element only; the others have no children here.
        if (StructureIndex.KidsAlreadyListed(_source, element, copy.ListedKids)) return null;

        PdfReference? page = element["Pg"] as PdfReference ?? inheritedPage;
        var kids = new PdfArray();
        var children = new List<PdfDictionary>();
        foreach (PdfObject kid in StructureIndex.Kids(_source, element))
        {
            if (!copy.Charge(ct))
            {
                _source.Budget.Diagnostics.Skipped(Compat.Format("A copy of the structure tree stopped after {0} children and references; the rest of its content goes untagged.", StructureIndex.MaxKids));
                break;
            }

            if (kid is PdfNumber { IsInteger: true })
            {
                if (page is not null && copy.Pages.ContainsKey(page.Number)) kids.Add(kid);
                continue;
            }

            if (_source.Resolve(kid) is not PdfDictionary item) continue;
            if (StructureIndex.IsContentReference(item))
            {
                if (CopyContentReference(item, page, copy.Pages, pageMap, batch, work) is PdfDictionary reference) kids.Add(reference);
            }
            else if (CopyElement(kid, page, depth + 1, copy, pageMap, batch, work, ct) is { } child)
            {
                kids.Add(new PdfReference(child.Number, 0));
                children.Add(child.Dictionary);
            }
        }

        if (kids.Count == 0) return null;

        var result = new PdfDictionary(element.Count);
        for (int i = 0; i < element.Count; i++)
        {
            (string key, PdfObject value) = element.EntryAt(i);
            switch (key)
            {
                case "K" or "P" or "Pg" or "ID":
                    // Rebuilt below or by the parent.
                    break;
                case "S" when !SameDocument && _source.Resolve(value) is PdfName type:
                    result.Set(key, new PdfName(Renamed(_roleRenames, type.Value)));
                    break;
                case "Ref":
                    // Names elements that may come later in the tree, so it waits for the full map.
                    copy.PendingReferences.Add((result, value));
                    break;
                case "C" when !SameDocument:
                    result.Set(key, RenamedClasses(value, copy, ct));
                    break;
                default:
                    result.Set(key, CopyValue(value, key, 1, pageMap, batch, work));
                    break;
            }
        }

        if (element["Pg"] is PdfReference own && copy.Pages.TryGetValue(own.Number, out int ownCopy)) result.Set("Pg", new PdfReference(ownCopy, 0));
        result.Set("K", kids);

        int copyNumber = _target.NextNumber++;
        _target.Objects[copyNumber] = result;
        foreach (PdfDictionary child in children) child.Set("P", new PdfReference(copyNumber, 0));
        if (number is int sourceNumber) copy.Map[sourceNumber] = copyNumber;
        copy.Elements.Add(result);

        // IDs are unique in a document (ISO 32000-1, 14.7.2). A copy within the same document
        // would repeat its original's, so it goes without one.
        if (!SameDocument && _source.Resolve(element["ID"]) is PdfString id)
        {
            PdfString unique = UniqueId(copy, id);
            result.Set("ID", unique);
            copy.Ids.Add((unique, copyNumber));
        }

        return (result, copyNumber);
    }

    /// <summary>
    /// Points the /Ref of copied elements at the copies of the elements they name (ISO 32000-1,
    /// Table 323). Runs once every element is copied, as a reference may name a later sibling.
    /// Elements that were not copied are left out, and so is a /Ref that names none.
    /// </summary>
    /// <remarks>
    /// Many elements can share one /Ref array held as an object. It is mapped once and the copies
    /// share the result, as the originals do; mapping it for each would cost their product.
    /// </remarks>
    private void ResolveReferences(StructureCopy copy, CancellationToken ct)
    {
        var mappedArrays = new Dictionary<int, PdfReference?>();
        foreach ((PdfDictionary element, PdfObject value) in copy.PendingReferences)
        {
            if (value is PdfReference shared && mappedArrays.TryGetValue(shared.Number, out PdfReference? done))
            {
                if (done is not null) element.Set("Ref", done);
                continue;
            }

            if (_source.Resolve(value) is not PdfArray array) continue;
            var mapped = new PdfArray();
            foreach (PdfObject item in array)
            {
                if (!copy.Charge(ct))
                {
                    _source.Budget.Diagnostics.Skipped(Compat.Format("A copy of the structure tree stopped after {0} children and references; the rest of its /Ref entries were left out.", StructureIndex.MaxKids));
                    return;
                }

                if (item is PdfReference r && StructureReference(r) is PdfReference target) mapped.Add(target);
            }

            if (value is PdfReference own)
            {
                done = mapped.Count > 0 ? _target.AddObject(mapped) : null;
                mappedArrays[own.Number] = done;
                if (done is not null) element.Set("Ref", done);
            }
            else if (mapped.Count > 0)
            {
                element.Set("Ref", mapped);
            }
        }
    }

    /// <summary>
    /// A marked content reference (MCR) or object reference (OBJR) whose page is in
    /// <paramref name="pages"/>, pointing at the copies; null when its page or object stays behind.
    /// </summary>
    private PdfDictionary? CopyContentReference(
        PdfDictionary item, PdfReference? inheritedPage, Dictionary<int, int> pages, Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work)
    {
        PdfReference? page = item["Pg"] as PdfReference ?? inheritedPage;
        if (page is null || !pages.TryGetValue(page.Number, out int pageCopy)) return null;

        var result = new PdfDictionary(item.Count);
        for (int i = 0; i < item.Count; i++)
        {
            (string key, PdfObject value) = item.EntryAt(i);
            if (key == "Pg")
            {
                result.Set(key, new PdfReference(pageCopy, 0));
            }
            else if (key == "Obj" && value is PdfReference target)
            {
                // An annotation is copied with its page, never on its own: one that did not come
                // along is not on an imported page.
                if (batch.TryGetValue(target.Number, out int annotation)) result.Set(key, new PdfReference(annotation, 0));
                else if (_source.Resolve(target) is PdfDictionary d && IsAnnotation(d)) return null;
                else result.Set(key, CopyValue(value, key, 1, pageMap, batch, work));
            }
            else
            {
                result.Set(key, CopyValue(value, key, 1, pageMap, batch, work));
            }
        }

        return result;
    }

    /// <summary>
    /// What a reference to a structure element of the source becomes while elements are being
    /// copied: its copy, or null when it was not copied. Null for anything else.
    /// </summary>
    private PdfObject? StructureReference(PdfReference r)
    {
        if (_structure is not { } copy) return null;
        if (r.Number == copy.Index.RootNumber) return SameDocument ? r : PdfNull.Instance;
        if (!copy.Index.IsElement(r.Number)) return null;
        if (copy.Map.TryGetValue(r.Number, out int number)) return new PdfReference(number, 0);
        return SameDocument ? r : PdfNull.Instance;
    }

    /// <summary>
    /// Once every copy exists: gives the pages of the round, given in <paramref name="pages"/>,
    /// their annotations and XObjects new keys in the target's parent tree, hangs the copied
    /// elements under the target's structure root, and merges the role map, class map, ID tree,
    /// mark information and language.
    /// </summary>
    private void FinishStructure(StructureCopy? copy, Dictionary<int, int> pages, Dictionary<int, int> batch, List<int> newlyShared, bool targetWasEmpty, CancellationToken ct)
    {
        _structure = null;
        if (copy is null)
        {
            // An untagged source: structure keys its XObjects carry index nothing in the target,
            // and would collide with the target's own keys.
            DropKeys(newlyShared, targetWasEmpty);
            return;
        }

        PdfDictionary? root = _target.Resolve(_target.Catalog["StructTreeRoot"]) as PdfDictionary;
        long first = root is null ? 0 : NextParentTreeKey(root);

        // Every page, annotation and XObject may take a key. A target whose keys leave no room
        // below the largest integer gets the pages untagged: Import has no new way to fail.
        long needed = pages.Count + batch.Count + (2L * (newlyShared.Count + _unkeyed.Count));
        if (first > int.MaxValue - needed)
        {
            _target.Budget.Diagnostics.Skipped("The target's parent tree has no keys left for the imported pages; they were copied untagged.");
            ResetUnmergedRenames();
            DropKeys(newlyShared, targetWasEmpty);
            return;
        }

        int next = (int)first;
        var entries = new List<(int Key, PdfObject Value)>();

        foreach ((int source, int target) in pages)
        {
            if (_source.Objects.TryGetValue(source, out PdfObject? page) && page is PdfDictionary d
                && copy.ParentTreeValue(_source, d["StructParents"]) is PdfObject value && _target.Objects[target] is PdfDictionary pageCopy)
            {
                entries.Add((next, value));
                pageCopy.Set("StructParents", PdfNumber.FromInteger(next++));
            }
        }

        foreach ((int source, int target) in batch)
        {
            if (_source.Objects.TryGetValue(source, out PdfObject? annotation) && annotation is PdfDictionary d
                && copy.ParentTreeValue(_source, d["StructParent"]) is PdfObject value && _target.Objects[target] is PdfDictionary annotationCopy)
            {
                entries.Add((next, value));
                annotationCopy.Set("StructParent", PdfNumber.FromInteger(next++));
            }
        }

        // XObjects are shared by every import from this source, so only those this call copied
        // first get keys, mapped to this call's elements, and those an earlier call left without.
        // What an XObject carries is decided by the source: the copy may have lost it.
        foreach (int source in newlyShared.Concat(_unkeyed).ToList())
        {
            PdfObject target = _target.Objects[_shared[source]];
            if (!_source.Objects.TryGetValue(source, out PdfObject? original) || original is not PdfStream stream || target is not PdfStream xobject) continue;
            bool lost = false;
            foreach (string key in XObjectStructureKeys)
            {
                if (!stream.Dictionary.ContainsKey(key)) continue;
                if (copy.ParentTreeValue(_source, stream.Dictionary[key]) is PdfObject value)
                {
                    entries.Add((next, value));
                    xobject.Dictionary.Set(key, PdfNumber.FromInteger(next++));
                }
                else
                {
                    xobject.Dictionary.Remove(key);
                    lost = true;
                }
            }

            if (lost) _unkeyed.Add(source);
            else _unkeyed.Remove(source);
        }

        if (copy.Tops.Count == 0 && entries.Count == 0)
        {
            ResetUnmergedRenames();
            CarryLanguage([], targetWasEmpty);
            return;
        }

        PdfReference rootReference = TargetRoot(ref root);
        AppendKids(root!, rootReference, copy.Tops);
        AppendParentTree(root!, entries, next);

        if (!SameDocument)
        {
            MergeRoleMap(root!, copy.Index);
            MergeClassMap(root!, copy.ClassMap);
            if (copy.Ids.Count > 0) MergeIdTree(root!, copy);
            if (_idRenames.Count > 0) RewriteHeaders(copy, ct);
            MergeMarkInfo();
            CarryLanguage([.. copy.Tops.Select(t => t.Dictionary)], targetWasEmpty);
        }
    }

    /// <summary>The target's structure root, created with the catalog entry pointing at it when there is none.</summary>
    private PdfReference TargetRoot(ref PdfDictionary? root)
    {
        if (root is not null && _target.Catalog["StructTreeRoot"] is PdfReference existing) return existing;

        // A root held directly in the catalog cannot be a parent: make it an object of its own.
        root ??= new PdfDictionary { ["Type"] = new PdfName("StructTreeRoot") };
        PdfReference reference = _target.AddObject(root);
        _target.Catalog.Set("StructTreeRoot", reference);
        return reference;
    }

    private void AppendKids(PdfDictionary root, PdfReference rootReference, List<(PdfDictionary Dictionary, int Number)> tops)
    {
        if (tops.Count == 0) return;

        // /K holds one element or an array of them; an array, direct or not, grows in place.
        PdfObject? k = root["K"];
        PdfObject? resolved = _target.Resolve(k);
        PdfArray kids = resolved as PdfArray ?? (resolved is null or PdfNull ? new PdfArray() : PdfArray.Of(k!));
        foreach ((PdfDictionary top, int number) in tops)
        {
            top.Set("P", rootReference);
            kids.Add(new PdfReference(number, 0));
        }

        if (resolved is not PdfArray) root.Set("K", kids);
    }

    /// <summary>
    /// The first parent tree key no entry uses (ISO 32000-1, 14.7.4.4). ParentTreeNextKey is a
    /// hint files often leave stale, so the keys themselves count too. A long, as a hostile
    /// target can use keys up to the largest integer.
    /// </summary>
    private long NextParentTreeKey(PdfDictionary root)
    {
        long next = _target.Resolve(root["ParentTreeNextKey"]) is PdfNumber { IsInteger: true } n && n.IntegerValue is > 0 and < int.MaxValue ? n.IntegerValue : 0;
        if (_target.Resolve(root["ParentTree"]) is PdfDictionary tree)
        {
            foreach ((PdfObject key, PdfObject _) in TreeReader.Entries(_target, tree, "Nums"))
            {
                if (key is PdfNumber { IsInteger: true } k && k.IntegerValue >= next && k.IntegerValue < long.MaxValue) next = k.IntegerValue + 1;
            }
        }

        return next;
    }

    /// <summary>
    /// Adds entries whose keys are all above the existing ones, so they go at the end: into the
    /// root's /Nums, or as a last leaf when the root has kids.
    /// </summary>
    private void AppendParentTree(PdfDictionary root, List<(int Key, PdfObject Value)> entries, int next)
    {
        if (entries.Count == 0) return;
        if (_target.Resolve(root["ParentTree"]) is not PdfDictionary tree)
        {
            tree = new PdfDictionary();
            root.Set("ParentTree", _target.AddObject(tree));
        }

        var nums = new PdfArray();
        foreach ((int key, PdfObject value) in entries)
        {
            nums.Add(PdfNumber.FromInteger(key));
            nums.Add(value);
        }

        if (tree["Nums"] is null && _target.Resolve(tree["Kids"]) is PdfArray kids)
        {
            var leaf = new PdfDictionary
            {
                ["Limits"] = PdfArray.Of(PdfNumber.FromInteger(entries[0].Key), PdfNumber.FromInteger(entries[^1].Key)),
                ["Nums"] = nums,
            };
            kids.Add(_target.AddObject(leaf));
        }
        else
        {
            PdfArray existing = _target.Resolve(tree["Nums"]) as PdfArray ?? new PdfArray();
            foreach (PdfObject item in nums) existing.Add(item);
            if (tree["Nums"] is not PdfReference) tree.Set("Nums", existing);
        }

        root.Set("ParentTreeNextKey", PdfNumber.FromInteger(next));
    }

    /// <summary>
    /// Names of the source's role map or class map that the target already uses differently,
    /// each with the first free name _2, _3 and so on. Role map entries that map to the same type
    /// are no conflict; a class name the target has is, as two attribute sets cannot be compared.
    /// </summary>
    private Dictionary<string, string> Renames(PdfDictionary sourceRoot, string mapKey, bool sameValueIsNoConflict)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_source.Resolve(sourceRoot[mapKey]) is not PdfDictionary source) return renames;
        PdfDictionary? target = _target.Resolve(_target.ResolveDictionary(_target.Catalog["StructTreeRoot"])?[mapKey]) as PdfDictionary;
        if (target is null) return renames;

        for (int i = 0; i < source.Count; i++)
        {
            (string name, PdfObject value) = source.EntryAt(i);
            if (!target.ContainsKey(name)) continue;
            if (sameValueIsNoConflict && _source.Resolve(value) is PdfName a && _target.Resolve(target[name]) is PdfName b && a.Value == b.Value) continue;
            string unique = name;
            for (int n = 2; target.ContainsKey(unique) || source.ContainsKey(unique); n++) unique = name + "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            renames[name] = unique;
        }

        return renames;
    }

    private static string Renamed(Dictionary<string, string>? renames, string name) => renames is not null && renames.TryGetValue(name, out string? to) ? to : name;

    /// <summary>An element's /C: a class name, or an array of names each optionally followed by a revision number.</summary>
    /// <remarks>
    /// Many elements can share one /C array held as an object. It is renamed once a round, into a
    /// new object the copies share, as the originals do; each name read is charged to the round's work.
    /// </remarks>
    private PdfObject RenamedClasses(PdfObject value, StructureCopy copy, CancellationToken ct)
    {
        if (value is PdfReference shared && copy.ClassArrays.TryGetValue(shared.Number, out PdfObject? done)) return done;
        PdfObject renamed;
        switch (_source.Resolve(value))
        {
            case PdfName name:
                renamed = new PdfName(Renamed(_classRenames, name.Value));
                break;
            case PdfArray array:
                var items = new List<PdfObject>(array.Count);
                foreach (PdfObject item in array)
                {
                    // Out of work, the element goes without its classes, and the elements after it are not copied.
                    if (!copy.Charge(ct))
                    {
                        _source.Budget.Diagnostics.Skipped(Compat.Format("A copy of the structure tree stopped after {0} children and references; the rest of its content goes untagged.", StructureIndex.MaxKids));
                        return PdfNull.Instance;
                    }

                    items.Add(_source.Resolve(item) is PdfName n ? new PdfName(Renamed(_classRenames, n.Value)) : item is PdfReference ? PdfNull.Instance : item);
                }

                renamed = value is PdfReference ? _target.AddObject(new PdfArray(items)) : new PdfArray(items);
                break;
            default:
                renamed = PdfNull.Instance;
                break;
        }

        if (value is PdfReference own) copy.ClassArrays[own.Number] = renamed;
        return renamed;
    }

    private void MergeRoleMap(PdfDictionary root, StructureIndex index)
    {
        if (_roleMapMerged || _source.Resolve(index.Root["RoleMap"]) is not PdfDictionary source) return;
        PdfDictionary target = _target.Resolve(root["RoleMap"]) as PdfDictionary ?? new PdfDictionary();
        for (int i = 0; i < source.Count; i++)
        {
            (string name, PdfObject value) = source.EntryAt(i);
            string key = Renamed(_roleRenames, name);
            if (!target.ContainsKey(key) && _source.Resolve(value) is PdfName type) target.Set(key, new PdfName(Renamed(_roleRenames, type.Value)));
        }

        if (root["RoleMap"] is not PdfReference) root.Set("RoleMap", target);
        _roleMapMerged = true;
    }

    private void MergeClassMap(PdfDictionary root, PdfDictionary? classes)
    {
        if (_classMapMerged || classes is null) return;
        PdfDictionary target = _target.Resolve(root["ClassMap"]) as PdfDictionary ?? new PdfDictionary();
        for (int i = 0; i < classes.Count; i++)
        {
            (string name, PdfObject value) = classes.EntryAt(i);
            if (!target.ContainsKey(name)) target.Set(name, value);
        }

        if (root["ClassMap"] is not PdfReference) root.Set("ClassMap", target);
        _classMapMerged = true;
    }

    /// <summary>Renames found for a call that copied nothing are worked out again next time, against the target as it is then.</summary>
    private void ResetUnmergedRenames()
    {
        if (!_roleMapMerged) _roleRenames = null;
        if (!_classMapMerged) _classRenames = null;
    }

    /// <summary>The IDs the target's elements already use.</summary>
    private HashSet<string> TargetIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (_target.ResolveDictionary(_target.ResolveDictionary(_target.Catalog["StructTreeRoot"])?["IDTree"]) is PdfDictionary tree)
        {
            foreach ((PdfObject key, PdfObject _) in TreeReader.Entries(_target, tree, "Names"))
            {
                if (_target.Resolve(key) is PdfString s) ids.Add(Compat.Latin1.GetString(s.Value));
            }
        }

        return ids;
    }

    /// <summary>The ID an element's copy carries: its own, or the first free one with a suffix _2, _3 and so on.</summary>
    private PdfString UniqueId(StructureCopy copy, PdfString id)
    {
        string original = Compat.Latin1.GetString(id.Value);
        HashSet<string> taken = copy.TargetIds ??= [];
        string unique = original;
        for (int n = 2; taken.Contains(unique); n++) unique = original + "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        taken.Add(unique);
        if (unique != original) _idRenames[original] = unique;
        return new PdfString(Compat.Latin1.GetBytes(unique));
    }

    /// <summary>
    /// Adds the copies' IDs to the target's ID tree (ISO 32000-1, 14.7.2), a name tree written
    /// again as one sorted leaf, as new keys may sort anywhere among the old.
    /// </summary>
    private void MergeIdTree(PdfDictionary root, StructureCopy copy)
    {
        // TODO(diagnostics): an ID tree larger than TreeReader reads loses its unread entries
        // here; report it once the diagnostics channel exists.
        var entries = new SortedDictionary<string, PdfObject>(StringComparer.Ordinal);
        if (_target.Resolve(root["IDTree"]) is PdfDictionary existing)
        {
            foreach ((PdfObject key, PdfObject value) in TreeReader.Entries(_target, existing, "Names"))
            {
                if (_target.Resolve(key) is PdfString s) entries[Compat.Latin1.GetString(s.Value)] = value;
            }
        }

        foreach ((PdfString id, int element) in copy.Ids) entries[Compat.Latin1.GetString(id.Value)] = new PdfReference(element, 0);
        var names = new PdfArray();
        foreach (KeyValuePair<string, PdfObject> entry in entries)
        {
            names.Add(new PdfString(Compat.Latin1.GetBytes(entry.Key)));
            names.Add(entry.Value);
        }

        root.Set("IDTree", _target.AddObject(new PdfDictionary { ["Names"] = names }));
    }

    /// <summary>
    /// Table cells name their header cells by ID (/Headers, ISO 32000-1, 14.8.5.7). Where an ID
    /// was renamed, the copied attributes follow it. Runs once the attribute objects are copied.
    /// </summary>
    /// <remarks>
    /// Attribute objects are shared by every copy of the source that refers to them, so a renamed
    /// one goes on the element as a copy of its own, never written in place. Within a round, cells
    /// that share an attribute object or an array share its renamed copy too, as a copy for each
    /// cell would cost cells times headers. Each header read is charged to the round's work.
    /// </remarks>
    private void RewriteHeaders(StructureCopy copy, CancellationToken ct)
    {
        var rewrite = new HeaderRewrite(copy, ct);
        PdfDictionary? classes = _target.ResolveDictionary(_target.ResolveDictionary(_target.Catalog["StructTreeRoot"])?["ClassMap"]);
        foreach (PdfDictionary element in copy.Elements)
        {
            if (element["A"] is PdfObject attributes && RenamedAttributes(attributes, rewrite) is PdfObject renamed) element.Set("A", renamed);
            if (classes is not null && element["C"] is PdfObject names && !rewrite.Refused) OverrideClassHeaders(element, names, classes, rewrite);
            if (rewrite.Refused)
            {
                _source.Budget.Diagnostics.Skipped(Compat.Format("A copy of the structure tree stopped after {0} children and references; the rest of its table cells keep the header IDs they had.", StructureIndex.MaxKids));
                return;
            }
        }
    }

    /// <summary>
    /// Gives an element whose /Headers come from an attribute class (ISO 32000-1, 14.7.5.2) an
    /// attribute object of its own with the renamed IDs, as attributes in /A take precedence over
    /// those of the classes. The class itself is left as it is: the copies of earlier imports use
    /// it with the IDs it names. No order is defined among an element's classes, so the first
    /// that gives /Headers is the one followed.
    /// </summary>
    private void OverrideClassHeaders(PdfDictionary element, PdfObject names, PdfDictionary classes, HeaderRewrite rewrite)
    {
        // /C holds a class name, or class names each optionally followed by a revision number.
        PdfDictionary? attribute = null;
        PdfNumber? revision = null;
        PdfObject resolved = _target.Resolve(names) ?? PdfNull.Instance;
        PdfArray list = resolved as PdfArray ?? PdfArray.Of(resolved);
        for (int i = 0; i < list.Count && attribute is null; i++)
        {
            if (list[i] is not PdfName name) continue;
            attribute = ClassHeaders(name.Value, classes, rewrite);
            revision = i + 1 < list.Count ? list[i + 1] as PdfNumber : null;
        }

        if (attribute is null || _target.Resolve(attribute["O"]) is not PdfName owner) return;
        if (!rewrite.Overrides.TryGetValue(attribute, out PdfReference? reference))
        {
            PdfObject? headers = Shared(attribute["Headers"]!, rewrite.Headers, resolved => resolved is PdfArray h ? RenamedHeaders(h, rewrite) : null);
            reference = headers is null ? null : _target.AddObject(new PdfDictionary { ["O"] = owner, ["Headers"] = headers });
            rewrite.Overrides[attribute] = reference;
        }

        if (reference is null) return;

        // The element's own /Headers for that owner win over the class's: nothing to override.
        // Otherwise its attribute objects stay, and the override goes at the end (14.7.5.1).
        var items = new List<PdfObject>();
        PdfObject? current = element["A"];
        switch (_target.Resolve(current))
        {
            case PdfDictionary single:
                if (GivesHeaders(single, owner)) return;
                items.Add(current!);
                break;
            case PdfArray array:
                foreach (PdfObject item in array)
                {
                    if (!rewrite.Charge()) return;
                    if (_target.ResolveDictionary(item) is PdfDictionary own && GivesHeaders(own, owner)) return;
                    items.Add(item);
                }

                break;
        }

        // The override repeats the class's attributes, so it keeps the class's revision number
        // (14.7.5.3): current, or out of date, as the class was for this element.
        items.Add(reference);
        if (revision is { IsInteger: true, IntegerValue: not 0 }) items.Add(revision);
        element.Set("A", items.Count == 1 ? reference : new PdfArray(items));
    }

    /// <summary>The first attribute object of a class that gives /Headers, looked up once a round; null when it gives none.</summary>
    private PdfDictionary? ClassHeaders(string name, PdfDictionary classes, HeaderRewrite rewrite)
    {
        if (rewrite.Classes.TryGetValue(name, out PdfDictionary? known)) return known;

        // A class map value is an attribute object or an array of them (14.7.5.2).
        PdfObject? value = _target.Resolve(classes[name]);
        IEnumerable<PdfObject> items = value switch
        {
            PdfArray array => array,
            null => [],
            _ => [value],
        };

        PdfDictionary? found = null;
        foreach (PdfObject item in items)
        {
            if (_target.ResolveDictionary(item) is PdfDictionary attribute && attribute.ContainsKey("Headers"))
            {
                found = attribute;
                break;
            }
        }

        rewrite.Classes[name] = found;
        return found;
    }

    private bool GivesHeaders(PdfDictionary attribute, PdfName owner) =>
        attribute.ContainsKey("Headers") && _target.Resolve(attribute["O"]) is PdfName o && o.Value == owner.Value;

    /// <summary>An element's /A, one attribute object or an array of them each optionally followed by a revision number, with renamed header IDs.</summary>
    private PdfObject? RenamedAttributes(PdfObject value, HeaderRewrite rewrite) => _target.Resolve(value) switch
    {
        PdfDictionary => RenamedAttribute(value, rewrite),
        PdfArray => Shared(value, rewrite.Lists, resolved => WithRenamedItems((PdfArray)resolved, rewrite)),
        _ => null,
    };

    private PdfObject? RenamedAttribute(PdfObject value, HeaderRewrite rewrite) =>
        Shared(value, rewrite.Attributes, resolved => resolved is PdfDictionary attribute ? WithRenamedHeaders(attribute, rewrite) : null);

    /// <summary>A copy of an /A array whose attribute objects use the renamed IDs, or null when none of them was renamed.</summary>
    private PdfArray? WithRenamedItems(PdfArray attributes, HeaderRewrite rewrite)
    {
        PdfArray? rewritten = null;
        for (int i = 0; i < attributes.Count && !rewrite.Refused; i++)
        {
            if (attributes[i] is not PdfNumber && RenamedAttribute(attributes[i], rewrite) is PdfObject renamed)
            {
                rewritten ??= new PdfArray([.. attributes.Items]);
                rewritten.Items[i] = renamed;
            }
        }

        return rewritten;
    }

    /// <summary>A copy of <paramref name="attribute"/> whose /Headers use the renamed IDs, or null when none of them was renamed.</summary>
    private PdfDictionary? WithRenamedHeaders(PdfDictionary attribute, HeaderRewrite rewrite)
    {
        if (attribute["Headers"] is not PdfObject value) return null;
        if (Shared(value, rewrite.Headers, resolved => resolved is PdfArray headers ? RenamedHeaders(headers, rewrite) : null) is not PdfObject renamed) return null;
        PdfDictionary copy = PdfDocument.Clone(attribute);
        copy.Set("Headers", renamed);
        return copy;
    }

    /// <summary>A copy of a /Headers array with the renamed IDs, or null when none was renamed or the round's work ran out.</summary>
    private PdfArray? RenamedHeaders(PdfArray headers, HeaderRewrite rewrite)
    {
        PdfArray? rewritten = null;
        for (int i = 0; i < headers.Count; i++)
        {
            if (!rewrite.Charge()) return null;
            if (_target.Resolve(headers[i]) is PdfString h && _idRenames.TryGetValue(Compat.Latin1.GetString(h.Value), out string? renamed))
            {
                rewritten ??= new PdfArray([.. headers.Items]);
                rewritten.Items[i] = new PdfString(Compat.Latin1.GetBytes(renamed));
            }
        }

        return rewritten;
    }

    /// <summary>
    /// The renamed copy of <paramref name="value"/>, made by <paramref name="rename"/> from what it
    /// resolves to, once a round: <paramref name="done"/> keeps it by the original. A value held
    /// as an object of its own is copied to a new object, which the elements sharing it share.
    /// Null when nothing in it was renamed.
    /// </summary>
    private PdfObject? Shared(PdfObject value, Dictionary<PdfObject, PdfObject?> done, Func<PdfObject, PdfObject?> rename)
    {
        if (_target.Resolve(value) is not PdfObject resolved) return null;
        if (done.TryGetValue(resolved, out PdfObject? known)) return known;
        PdfObject? copy = rename(resolved);
        PdfObject? result = copy is not null && value is PdfReference ? _target.AddObject(copy) : copy;
        done[resolved] = result;
        return result;
    }

    /// <summary>The target is tagged once tagged content arrives (ISO 32000-1, 14.7.1), and suspect when any source was.</summary>
    private void MergeMarkInfo()
    {
        if (_source.ResolveDictionary(_source.Catalog["MarkInfo"]) is not PdfDictionary source) return;
        PdfDictionary target = _target.ResolveDictionary(_target.Catalog["MarkInfo"]) is PdfDictionary existing ? PdfDocument.Clone(existing) : new PdfDictionary();
        foreach (string flag in MarkInfoFlags)
        {
            if (_source.Resolve(source[flag]) is PdfBoolean { Value: true }) target.Set(flag, PdfBoolean.True);
        }

        if (target.Count > 0) _target.Catalog.Set("MarkInfo", target);
    }

    /// <summary>
    /// Keeps the source's natural language (ISO 32000-1, 14.9.2). An empty target takes it as its
    /// own; a target in another language gets it on the copied top level elements instead, which
    /// override the document's language for everything below them.
    /// </summary>
    private void CarryLanguage(List<PdfDictionary> tops, bool targetWasEmpty)
    {
        if (SameDocument || _source.Language is not string language) return;
        string? current = _target.Language;
        if (current is null && targetWasEmpty)
        {
            _target.Language = language;
            return;
        }

        if (string.Equals(current, language, StringComparison.OrdinalIgnoreCase)) return;
        foreach (PdfDictionary top in tops)
        {
            if (top["Lang"] is null) top.Set("Lang", PdfString.FromText(language));
        }

        // TODO(diagnostics): untagged content imported into a document in another language
        // keeps no record of its own; report it once the diagnostics channel exists.
    }

    /// <summary>The import brings no structure: the shared XObjects it copied lose their keys, and the language is carried.</summary>
    private void DropKeys(List<int> newlyShared, bool targetWasEmpty)
    {
        foreach (int source in newlyShared) DropStructureKeys(_target.Objects[_shared[source]]);
        CarryLanguage([], targetWasEmpty);
    }

    /// <summary>Removes parent tree keys a copied XObject brought from a source whose tree does not come along.</summary>
    private static void DropStructureKeys(PdfObject copy)
    {
        if (copy is not PdfStream stream) return;
        stream.Dictionary.Remove("StructParents");
        stream.Dictionary.Remove("StructParent");
    }

    /// <summary>
    /// The renamed header copies of one round, by the object they copy, with one table per kind of
    /// object, as a hostile file can use one array both as an /A array and as a /Headers array.
    /// </summary>
    private sealed class HeaderRewrite(StructureCopy copy, CancellationToken ct)
    {
        public Dictionary<PdfObject, PdfObject?> Lists { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<PdfObject, PdfObject?> Attributes { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<PdfObject, PdfObject?> Headers { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Class name to its first attribute object that gives /Headers.</summary>
        public Dictionary<string, PdfDictionary?> Classes { get; } = new(StringComparer.Ordinal);

        /// <summary>Class attribute object to the override that elements using it get, null when it names no renamed ID.</summary>
        public Dictionary<PdfDictionary, PdfReference?> Overrides { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>True once the round's work ran out: the rewrite stops there.</summary>
        public bool Refused { get; private set; }

        public bool Charge()
        {
            if (copy.Charge(ct)) return true;
            Refused = true;
            return false;
        }
    }

    /// <summary>The structure copied by one round of an Import call.</summary>
    internal sealed class StructureCopy(StructureIndex index, HashSet<int> kept, Dictionary<int, int> pages)
    {
        public StructureIndex Index { get; } = index;

        /// <summary>Source page number to the number of its copy, for the pages of the round; content elsewhere stays behind.</summary>
        public Dictionary<int, int> Pages { get; } = pages;

        /// <summary>Indirect elements with content on the imported pages, and their ancestors.</summary>
        public HashSet<int> Kept { get; } = kept;

        public HashSet<int> Visited { get; } = [];

        /// <summary>Source element number to the number of its copy.</summary>
        public Dictionary<int, int> Map { get; } = [];

        /// <summary>Copies of the root's kids, in order, with their numbers.</summary>
        public List<(PdfDictionary Dictionary, int Number)> Tops { get; } = [];

        /// <summary>Every element copy, for passes that run once the work queue has drained.</summary>
        public List<PdfDictionary> Elements { get; } = [];

        public List<(PdfString Id, int Element)> Ids { get; } = [];

        public HashSet<string>? TargetIds { get; set; }

        public PdfDictionary? ClassMap { get; set; }

        public int Copied { get; set; }

        /// <summary>Copies whose /Ref is resolved once all elements are copied, with the source's value.</summary>
        public List<(PdfDictionary Element, PdfObject Value)> PendingReferences { get; } = [];

        /// <summary>Elements written in place that were copied, by identity.</summary>
        public HashSet<PdfDictionary> DirectVisited { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Source /C values held as objects of their own, to what the copies hold instead.</summary>
        public Dictionary<int, PdfObject> ClassArrays { get; } = [];

        /// <summary>Indirect /K arrays some element already listed.</summary>
        public HashSet<int> ListedKids { get; } = [];

        /// <summary>
        /// Children listed and /Ref, /C and /Headers entries read so far, bounded together by
        /// <see cref="StructureIndex.MaxKids"/>, so the work of a round is bounded however the
        /// file shares its arrays.
        /// </summary>
        public long Work { get; private set; }

        /// <summary>True once a unit of work was refused: what asked for it was left out.</summary>
        public bool Spent { get; private set; }

        /// <summary>
        /// Counts one unit of work about to be done, looking at the token every
        /// <see cref="StructureIndex.KidInterval"/> units. False once the round has done
        /// <see cref="StructureIndex.MaxKids"/>; the caller stops and reports what it left out.
        /// </summary>
        public bool Charge(CancellationToken ct)
        {
            if (Work >= StructureIndex.MaxKids)
            {
                Spent = true;
                return false;
            }

            if (++Work % StructureIndex.KidInterval == 0) ct.ThrowIfCancellationRequested();
            return true;
        }

        /// <summary>
        /// The parent tree value for a copy whose source key is <paramref name="key"/>: for
        /// content, an array of the copies of the elements its MCIDs belong to; for an
        /// annotation or XObject, the copy of its element. Null when nothing it names was copied.
        /// </summary>
        public PdfObject? ParentTreeValue(PdfDocument source, PdfObject? key)
        {
            if (source.Resolve(key) is not PdfNumber { IsInteger: true } k || k.IntegerValue is < 0 or > int.MaxValue) return null;
            if (!Index.ParentTree.TryGetValue((int)k.IntegerValue, out PdfObject? value)) return null;

            // Content keys hold an array, often an object of its own; annotation and XObject keys hold an element.
            if (source.Resolve(value) is not PdfArray array)
            {
                return value is PdfReference single && Map.TryGetValue(single.Number, out int element) ? new PdfReference(element, 0) : null;
            }

            var mapped = new PdfArray();
            bool any = false;
            foreach (PdfObject item in array)
            {
                if (item is PdfReference r && Map.TryGetValue(r.Number, out int copy))
                {
                    mapped.Add(new PdfReference(copy, 0));
                    any = true;
                }
                else
                {
                    mapped.Add(PdfNull.Instance);
                }
            }

            return any ? mapped : null;
        }
    }
}
