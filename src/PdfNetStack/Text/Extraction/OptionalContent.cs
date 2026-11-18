using PdfNetStack.Objects;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Which optional content groups (layers) are on in a document's default configuration
/// (ISO 32000-1, 8.11.4.3): /OCProperties /D, its /BaseState and its /ON and /OFF arrays.
/// </summary>
/// <remarks>
/// Visibility expressions (/VE) and anything that cannot be read count as visible, so text is never
/// dropped on a guess.
/// </remarks>
internal sealed class OptionalContent
{
    // Groups read from one /ON, /OFF or /OCGs array.
    private const int MaxGroups = 4096;

    private readonly PdfDocument _document;
    private readonly HashSet<PdfDictionary> _off = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _on = new(ReferenceEqualityComparer.Instance);
    private readonly bool _baseOff;

    private OptionalContent(PdfDocument document, bool baseOff)
    {
        _document = document;
        _baseOff = baseOff;
    }

    /// <summary>No group is off: a document without optional content.</summary>
    public static OptionalContent None(PdfDocument document) => new(document, false);

    public static OptionalContent Read(PdfDocument document)
    {
        if (document.Resolve(document.Catalog["OCProperties"]) is not PdfDictionary properties || document.Resolve(properties["D"]) is not PdfDictionary config)
        {
            return None(document);
        }

        var result = new OptionalContent(document, (document.Resolve(config["BaseState"]) as PdfName)?.Value == "OFF");
        Collect(document, config["OFF"], result._off);
        Collect(document, config["ON"], result._on);
        return result;
    }

    private static void Collect(PdfDocument document, PdfObject? entry, HashSet<PdfDictionary> into)
    {
        if (document.Resolve(entry) is not PdfArray groups) return;
        for (int i = 0; i < groups.Count && i < MaxGroups; i++)
        {
            if (document.Resolve(groups[i]) is PdfDictionary group) into.Add(group);
        }
    }

    /// <summary>Whether content under an /OC entry naming <paramref name="entry"/>, a group or a membership dictionary, is shown.</summary>
    public bool IsVisible(PdfDictionary entry)
    {
        if (entry.GetName("Type") != "OCMD") return IsOn(entry);

        // A membership dictionary (8.11.2.2): its groups under a policy, AnyOn by default. A /VE
        // expression is not evaluated: the content counts as shown.
        if (entry["VE"] is not null) return true;
        var groups = new List<PdfDictionary>();
        switch (_document.Resolve(entry["OCGs"]))
        {
            case PdfDictionary one:
                groups.Add(one);
                break;
            case PdfArray many:
                for (int i = 0; i < many.Count && i < MaxGroups; i++)
                {
                    if (_document.Resolve(many[i]) is PdfDictionary g) groups.Add(g);
                }

                break;
        }

        if (groups.Count == 0) return true;
        return (_document.Resolve(entry["P"]) as PdfName)?.Value switch
        {
            "AllOn" => groups.TrueForAll(IsOn),
            "AnyOff" => !groups.TrueForAll(IsOn),
            "AllOff" => !groups.Exists(IsOn),
            _ => groups.Exists(IsOn),
        };
    }

    private bool IsOn(PdfDictionary group) => _baseOff ? _on.Contains(group) : !_off.Contains(group);
}
