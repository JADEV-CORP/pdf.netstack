using PdfNetStack.Objects;
using PdfNetStack.Parsing;

namespace PdfNetStack.Documents;

/// <summary>
/// Copies pages from one document into another, or duplicates pages within a document.
/// Shared resources (fonts, images, color spaces) are copied once per source document and reused
/// by every later import. Annotations are copied fresh for each page, because an annotation
/// belongs to exactly one page. References to pages are remapped to their copies when those pages
/// are imported in the same call, and become null otherwise. Form fields come along with their
/// widgets (see <see cref="ImportFields"/>), and the part of the structure tree that tags the
/// pages' content with them (see <see cref="ImportStructure"/>).
/// </summary>
internal sealed partial class PageImporter
{
    // Keys that never travel with a page: its place in the old tree, its slot in the old
    // structure tree, and article beads that point into the old document's threads.
    private static readonly HashSet<string> DroppedPageKeys = new(StringComparer.Ordinal) { "Parent", "StructParents", "B" };
    private static readonly HashSet<string> AnnotationKeys = new(StringComparer.Ordinal) { "Annots", "Popup", "IRT" };

    // Objects copied between two looks at the cancellation token.
    private const int CancellationInterval = 256;

    private readonly PdfDocument _source;
    private readonly PdfDocument _target;
    private readonly Dictionary<int, int> _shared = new();

    // Source numbers of the shared objects the current call copied first, so their structure
    // keys can be remapped once the copies exist. Null outside Import.
    private List<int>? _newlyShared;

    public PageImporter(PdfDocument source, PdfDocument target)
    {
        _source = source;
        _target = target;
    }

    private bool SameDocument => ReferenceEquals(_source, _target);

    public IReadOnlyList<PdfPage> Import(List<PdfPage> pages, CancellationToken ct = default)
    {
        if (!SameDocument && !_source.Security.HasOwnerAccess && !_source.Security.Permissions.HasFlag(PdfPermissions.CopyContent))
        {
            throw new InvalidOperationException("The source document does not permit copying its content. Open it with the owner password.");
        }

        // Everything this call allocates is numbered from here, so a failure can take it back.
        int firstNumber = _target.NextNumber;
        var idRenames = new Dictionary<string, string>(_idRenames, StringComparer.Ordinal);
        var unkeyed = new HashSet<int>(_unkeyed);
        bool roleMapMerged = _roleMapMerged, classMapMerged = _classMapMerged;

        // Reserve numbers for every page first so links between them can be remapped.
        var firstCopies = new Dictionary<int, int>();
        var numbers = new int[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            numbers[i] = _target.NextNumber++;
            firstCopies.TryAdd(pages[i].Number, numbers[i]);
        }

        var result = new PdfPage[pages.Count];
        _newlyShared = [];

        // A document copying its own pages changes its tree with every call: read it again, as a
        // page copied earlier, or tagged since, is not in an older reading.
        if (SameDocument) _sourceStructureRead = false;

        // A document still empty takes the source's language as its own.
        bool targetWasEmpty = !SameDocument && _target.Pages.Count == 0;
        FormState form = SaveFormState();
        TargetState structure = SaveStructureState();
        try
        {
            foreach (List<int> round in Rounds(pages)) CopyRound(pages, round, numbers, firstCopies, result, targetWasEmpty, ct);
        }
        catch
        {
            // A failed or cancelled import leaves the target as it was, whatever rounds finished.
            // The shared copies this call reserved would otherwise be reused by the next import
            // while some never came to exist, and renames worked out against the target's role
            // map, class map and IDs would be applied to a call that finds them changed by another
            // source.
            foreach (int source in _newlyShared) _shared.Remove(source);
            RestoreFormState(form);
            structure.Restore();
            _structure = null;
            _roleMapMerged = roleMapMerged;
            _classMapMerged = classMapMerged;
            ResetUnmergedRenames();
            _unkeyed.Clear();
            _unkeyed.UnionWith(unkeyed);
            _idRenames.Clear();
            foreach (KeyValuePair<string, string> rename in idRenames) _idRenames[rename.Key] = rename.Value;
            for (int number = firstNumber; number < _target.NextNumber; number++) _target.Objects.Remove(number);
            _target.NextNumber = firstNumber;
            throw;
        }
        finally
        {
            _newlyShared = null;
        }

        return result;
    }

    /// <summary>
    /// Indexes into <paramref name="pages"/>, in rounds that copy each page at most once. A page
    /// listed again waits for a later round, so each copy gets its own annotations, fields and
    /// structure elements.
    /// </summary>
    private static List<List<int>> Rounds(List<PdfPage> pages)
    {
        var rounds = new List<List<int>>();
        var occurrences = new Dictionary<int, int>();
        for (int i = 0; i < pages.Count; i++)
        {
            occurrences.TryGetValue(pages[i].Number, out int round);
            occurrences[pages[i].Number] = round + 1;
            if (round == rounds.Count) rounds.Add([]);
            rounds[round].Add(i);
        }

        return rounds;
    }

    private void CopyRound(
        List<PdfPage> pages, List<int> round, int[] numbers, Dictionary<int, int> firstCopies, PdfPage[] result, bool targetWasEmpty, CancellationToken ct)
    {
        // The pages this round copies, which its structure elements tag, and where links to
        // pages go: a page of this round links to its own copy, any other to its first copy.
        var copied = new Dictionary<int, int>(round.Count);
        var pageMap = new Dictionary<int, int>(firstCopies);
        foreach (int i in round)
        {
            copied[pages[i].Number] = numbers[i];
            pageMap[pages[i].Number] = numbers[i];
        }

        // Shared objects copied by an earlier round are this call's too, but only those this round
        // copies first get structure keys from it.
        int sharedBefore = _newlyShared!.Count;
        var batch = new Dictionary<int, int>();
        var work = new Queue<(PdfObject Source, int Target)>();
        foreach (int i in round)
        {
            ct.ThrowIfCancellationRequested();
            var copy = new PdfDictionary(pages[i].Dictionary.Count);
            for (int k = 0; k < pages[i].Dictionary.Count; k++)
            {
                (string key, PdfObject value) = pages[i].Dictionary.EntryAt(k);
                if (DroppedPageKeys.Contains(key)) continue;
                copy.Set(key, CopyValue(value, key, 0, pageMap, batch, work));
            }

            _target.Objects[numbers[i]] = copy;
            result[i] = new PdfPage(_target, numbers[i], copy);
        }

        List<(int Target, string Key, PdfObject Value)> fixups = ImportFields(pageMap, batch, work);
        StructureCopy? structure = ImportStructure(copied, pageMap, batch, work, ct);
        for (int done = 0; work.Count > 0; done++)
        {
            if (done % CancellationInterval == 0) ct.ThrowIfCancellationRequested();
            (PdfObject source, int target) = work.Dequeue();
            _target.Objects[target] = source switch
            {
                PdfStream s => new PdfStream(CopyDictionary(s.Dictionary, 0, pageMap, batch, work), s.Data),
                PdfDictionary d => CopyDictionary(d, 0, pageMap, batch, work),
                _ => CopyValue(source, null, 0, pageMap, batch, work),
            };
        }

        foreach ((int target, string key, PdfObject value) in fixups)
        {
            if (_target.Objects[target] is PdfDictionary d) d.Set(key, value);
        }

        FinishStructure(structure, copied, batch, _newlyShared.GetRange(sharedBefore, _newlyShared.Count - sharedBefore), targetWasEmpty, ct);
    }

    /// <summary>What importing fields changes in the target's form: the catalog entry, the form's entries and its two arrays.</summary>
    private sealed record FormState(PdfObject? Entry, PdfDictionary? Form, PdfDictionary? Entries, PdfArray? Fields, List<PdfObject>? FieldItems, PdfArray? Order, List<PdfObject>? OrderItems);

    private FormState SaveFormState()
    {
        PdfObject? entry = _target.Catalog["AcroForm"];
        if (_target.Resolve(entry) is not PdfDictionary form) return new FormState(entry, null, null, null, null, null, null);
        PdfArray? fields = _target.Resolve(form["Fields"]) as PdfArray;
        PdfArray? order = _target.Resolve(form["CO"]) as PdfArray;
        return new FormState(entry, form, PdfDocument.Clone(form), fields, fields is null ? null : [.. fields.Items], order, order is null ? null : [.. order.Items]);
    }

    private void RestoreFormState(FormState state)
    {
        if (state.Entry is null) _target.Catalog.Remove("AcroForm");
        else _target.Catalog.Set("AcroForm", state.Entry);
        if (state.Form is null || state.Entries is null) return;
        state.Form.Clear();
        for (int i = 0; i < state.Entries.Count; i++)
        {
            (string key, PdfObject value) = state.Entries.EntryAt(i);
            state.Form.Set(key, value);
        }

        // The arrays may be shared objects the import added to in place.
        Restore(state.Fields, state.FieldItems);
        Restore(state.Order, state.OrderItems);

        static void Restore(PdfArray? array, List<PdfObject>? items)
        {
            if (array is null || items is null) return;
            array.Items.Clear();
            array.Items.AddRange(items);
        }
    }

    /// <summary>
    /// Objects of the target an import changes in place, as they were before it: the entries of
    /// dictionaries, and the length of arrays that only grow at the end.
    /// </summary>
    private sealed class TargetState
    {
        private readonly List<(PdfDictionary Dictionary, PdfDictionary Entries)> _dictionaries = [];
        private readonly List<(PdfArray Array, int Count)> _arrays = [];

        public void Save(PdfDictionary? dictionary)
        {
            if (dictionary is not null) _dictionaries.Add((dictionary, PdfDocument.Clone(dictionary)));
        }

        public void Save(PdfArray? array)
        {
            if (array is not null) _arrays.Add((array, array.Count));
        }

        public void Restore()
        {
            foreach ((PdfArray array, int count) in _arrays)
            {
                if (array.Count > count) array.Items.RemoveRange(count, array.Count - count);
            }

            foreach ((PdfDictionary dictionary, PdfDictionary entries) in _dictionaries)
            {
                dictionary.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    (string key, PdfObject value) = entries.EntryAt(i);
                    dictionary.Set(key, value);
                }
            }
        }
    }

    /// <summary>
    /// Copies an object and everything it references, such as a page's resources, into the
    /// target. Objects already copied from this source are reused; references to pages become null.
    /// </summary>
    public PdfObject ImportObject(PdfObject value)
    {
        if (!SameDocument && !_source.Security.HasOwnerAccess && !_source.Security.Permissions.HasFlag(PdfPermissions.CopyContent))
        {
            throw new InvalidOperationException("The source document does not permit copying its content. Open it with the owner password.");
        }

        var pageMap = new Dictionary<int, int>();
        var batch = new Dictionary<int, int>();
        var work = new Queue<(PdfObject Source, int Target)>();
        PdfObject copy = CopyValue(value, null, 0, pageMap, batch, work);
        while (work.Count > 0)
        {
            (PdfObject source, int target) = work.Dequeue();
            _target.Objects[target] = source switch
            {
                PdfStream s => new PdfStream(CopyDictionary(s.Dictionary, 0, pageMap, batch, work), s.Data),
                PdfDictionary d => CopyDictionary(d, 0, pageMap, batch, work),
                _ => CopyValue(source, null, 0, pageMap, batch, work),
            };
        }

        return copy;
    }

    private PdfDictionary CopyDictionary(PdfDictionary d, int depth, Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work)
    {
        var copy = new PdfDictionary(d.Count);
        bool annotation = IsAnnotation(d);
        for (int i = 0; i < d.Count; i++)
        {
            (string key, PdfObject value) = d.EntryAt(i);

            // An annotation's slot in the source structure tree means nothing in the copy. A
            // widget's field is rebuilt by ImportFields.
            if (annotation && key == "StructParent") continue;
            if (annotation && key == "Parent" && d.GetName("Subtype") == "Widget") continue;
            copy.Set(key, CopyValue(value, key, depth + 1, pageMap, batch, work));
        }

        return copy;
    }

    private PdfObject CopyValue(PdfObject value, string? key, int depth, Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work)
    {
        if (depth > PdfParser.MaxNesting) throw new PdfFormatException("Object nesting exceeds the supported depth.");
        switch (value)
        {
            case PdfReference r:
                return MapReference(r, key, pageMap, batch, work);
            case PdfArray a:
            {
                var items = new List<PdfObject>(a.Count);
                foreach (PdfObject item in a.Items) items.Add(CopyValue(item, key, depth + 1, pageMap, batch, work));
                return new PdfArray(items);
            }

            case PdfDictionary d:
                return CopyDictionary(d, depth, pageMap, batch, work);
            default:
                // Names, numbers, strings, booleans and null are immutable and can be shared.
                return value;
        }
    }

    private PdfObject MapReference(PdfReference r, string? key, Dictionary<int, int> pageMap, Dictionary<int, int> batch, Queue<(PdfObject, int)> work)
    {
        if (!_source.Objects.TryGetValue(r.Number, out PdfObject? target)) return PdfNull.Instance;

        if (pageMap.TryGetValue(r.Number, out int copiedPage)) return new PdfReference(copiedPage, 0);
        if (_source.IsPageTreeNode(r.Number)) return PdfNull.Instance;
        if (_source.IsPage(r.Number)) return SameDocument ? r : PdfNull.Instance;

        // A structure element is copied only with the content it tags; a generic copy would drag
        // its parent, and so the whole tree, along.
        if (StructureReference(r) is PdfObject mapped) return mapped;

        PdfDictionary? dict = target switch
        {
            PdfDictionary d => d,
            PdfStream s => s.Dictionary,
            _ => null,
        };

        // Annotations belong to one page: copy them for every import, never share.
        if ((key is not null && AnnotationKeys.Contains(key)) || (dict is not null && IsAnnotation(dict)))
        {
            if (!batch.TryGetValue(r.Number, out int fresh))
            {
                fresh = _target.NextNumber++;
                batch[r.Number] = fresh;
                work.Enqueue((target, fresh));
            }

            return new PdfReference(fresh, 0);
        }

        if (SameDocument) return r;

        if (!_shared.TryGetValue(r.Number, out int number))
        {
            number = _target.NextNumber++;
            _shared[r.Number] = number;
            _newlyShared?.Add(r.Number);
            work.Enqueue((target, number));
        }

        return new PdfReference(number, 0);
    }

    private static bool IsAnnotation(PdfDictionary d) =>
        d.GetName("Type") == "Annot" || (d["Subtype"] is PdfName && d["Rect"] is not null && d["Type"] is null or PdfName { Value: "Annot" });
}
