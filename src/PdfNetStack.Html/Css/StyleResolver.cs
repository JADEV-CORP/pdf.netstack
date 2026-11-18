using System.Globalization;
using PdfNetStack.Html.Dom;

namespace PdfNetStack.Html.Css;

internal enum CssOrigin
{
    UserAgent,
    Author,
}

/// <summary>A value for every property, indexed by property id or name.</summary>
internal sealed class PropertyValues : IEnumerable<KeyValuePair<string, List<CssValue>>>
{
    private readonly List<CssValue>[] _values = new List<CssValue>[CssProperties.ById.Length];

    public List<CssValue> this[int id]
    {
        get => _values[id];
        set => _values[id] = value;
    }

    public List<CssValue> this[string property]
    {
        get => _values[CssProperties.All[property].Id];
        set => _values[CssProperties.All[property].Id] = value;
    }

    public IEnumerator<KeyValuePair<string, List<CssValue>>> GetEnumerator()
    {
        for (int i = 0; i < _values.Length; i++) yield return new(CssProperties.ById[i].Name, _values[i]);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The computed style of an element or pseudo-element: every property has a value, lengths are in
/// CSS px, font-size and font-weight are numbers. Percentages, auto and math with percentages are
/// left for layout.
/// </summary>
internal sealed class ComputedStyle
{
    internal ComputedStyle(ComputedStyle? parent, string? pseudoElement)
    {
        Parent = parent;
        PseudoElement = pseudoElement;
        Custom = parent?.Custom ?? CustomProperties.Empty;
        Language = parent?.Language;
    }

    public ComputedStyle? Parent { get; }

    public string? PseudoElement { get; }

    internal PropertyValues Values { get; } = new();

    /// <summary>Custom properties, shared with the parent until this element sets its own, and then mostly still shared.</summary>
    internal CustomProperties Custom { get; set; }

    /// <summary>The nearest lang attribute of the element and its ancestors, as the text says it; null with none.</summary>
    public string? Language { get; internal set; }

    /// <summary>The font size in px.</summary>
    public double FontSize { get; internal set; } = 16;

    public List<CssValue> this[string property] => Values[property];

    /// <summary>The first identifier of a value, lowercase, or empty.</summary>
    public string Keyword(string property)
    {
        foreach (CssValue v in Values[property])
        {
            if (v.IsToken && v.Token.Type == CssTokenType.Ident) return v.LowerValue;
        }

        return "";
    }

    /// <summary>All identifiers of a value, lowercase and space separated, such as "inline flow-root".</summary>
    public string Keywords(string property)
    {
        string? first = null;
        List<string>? all = null;
        foreach (CssValue v in Values[property])
        {
            if (!v.IsToken || v.Token.Type != CssTokenType.Ident) continue;
            if (first is null) first = v.LowerValue;
            else (all ??= [first]).Add(v.LowerValue);
        }

        return all is null ? first ?? "" : string.Join(" ", all);
    }

    /// <summary>A length in px, a percentage of <paramref name="basis"/>, or null for auto, none and the like.</summary>
    public double? Length(string property, double basis = 0)
    {
        List<CssValue> value = Values[property];
        if (value.Count != 1) return null;
        CssValue v = value[0];
        if (CssGrammar.IsMath(v)) return CssMath.Evaluate(v, basis, out _);
        if (!v.IsToken) return null;
        return v.Token.Type switch
        {
            CssTokenType.Dimension when v.Token.Unit == "px" => v.Token.Number,
            CssTokenType.Number when v.Token.Number == 0 => 0,
            CssTokenType.Percentage => v.Token.Number / 100 * basis,
            _ => null,
        };
    }

    public double Number(string property)
    {
        List<CssValue> value = Values[property];
        return value.Count == 1 && value[0].IsToken && value[0].Token.Type == CssTokenType.Number ? value[0].Token.Number : 0;
    }

    /// <summary>A color, with currentcolor resolved to the color property.</summary>
    public CssColor Color(string property = "color")
    {
        List<CssValue> value = Values[property];
        CssColor? color = value.Count == 1 ? value[0].Color : null;
        if (color is not CssColor c) return CssColor.Black;
        return c.IsCurrentColor ? (property == "color" ? CssColor.Black : Color()) : c;
    }

    public override string ToString() => string.Join("; ", Values.Select(p => p.Key + ": " + string.Concat(p.Value.Select(v => v.ToString()))));
}

/// <summary>
/// Collects style sheets and computes styles (CSS Cascading Level 4): origins and importance,
/// specificity, order, presentational hints, style attributes, inheritance, var() and
/// computed values.
/// </summary>
internal sealed class StyleResolver
{
    // Bounds that keep hostile style sheets in check.
    private const int MaxImportDepth = 8;
    private const int MaxImports = 64;
    private const int MaxVarDepth = 32;
    private const int MaxVarOutput = 50_000;

    // Lengths are clamped to this many px either way, about 260 m: past any page, yet small
    // enough that positions summed over a whole document stay exact and valid PDF numbers.
    internal const double MaxLength = 1_000_000;

    // Font sizes stop at 10000px, as in browsers; unitless line heights at 1000 times the font size.
    internal const double MaxFontSize = 10_000;
    private const double MaxLineHeight = 1000;

    private readonly CssMedia _media;
    private readonly WorkBudget _budget;
    private readonly AncestorFilter _ancestors = new();
    private readonly List<CssValue>[] _initial;

    // Rules for elements and rules for pseudo-elements apart: an element is asked about ::before
    // and ::after too, and few rules target them.
    private readonly RuleIndex _elementRules = new();
    private readonly RuleIndex _pseudoElementRules = new();
    private int _order = 1;
    private int _imports;
    private double _rootFontSize = 16;

    /// <param name="media">The media queries are evaluated against.</param>
    /// <param name="budget">Counts selector tests and substitutions; unlimited when null.</param>
    public StyleResolver(CssMedia media, WorkBudget? budget = null)
    {
        _media = media;
        _budget = budget ?? WorkBudget.Unlimited();

        // Initial values hold no relative units, so they compute once per resolver.
        var probe = new ComputedStyle(null, null);
        _initial = [.. CssProperties.ById.Select(p => ComputeValue(probe, null, p.Name, p.Initial))];
        AddStyleSheet(UserAgent.StyleSheet, CssOrigin.UserAgent);
    }

    public List<CssPageRule> PageRules { get; } = [];

    public List<CssFontFaceRule> FontFaces { get; } = [];

    private sealed record Entry(CssSelector Selector, List<CssDeclaration> Declarations, CssOrigin Origin, int Order);

    /// <summary>
    /// Makes a URL from a style sheet absolute: resolved against the sheet's own absolute URL, or
    /// the document's base when that is null; null when there is nothing to resolve against.
    /// When set, sheets fetched by URL, linked or imported, have their relative URLs resolved
    /// against their own address; without it they resolve against the document's.
    /// </summary>
    public Func<string, string?, string?>? ResolveUrl { get; init; }

    /// <summary>Adds a style sheet; <paramref name="load"/> fetches the text of @import targets.</summary>
    public void AddStyleSheet(string css, CssOrigin origin, Func<string, string?>? load = null) =>
        AddRules(CssParser.ParseStyleSheet(css), origin, load, 0);

    /// <summary>Fetches a linked or imported style sheet and adds it, its own URLs made absolute against its address.</summary>
    private void AddFetched(string url, CssOrigin origin, Func<string, string?> load, int depth)
    {
        string? absolute = ResolveUrl?.Invoke(url, null);
        if (load(absolute ?? url) is not string css) return;
        Func<string, string?>? rebase = absolute is null || ResolveUrl is null ? null : relative => ResolveUrl(relative, absolute);
        AddRules(CssParser.ParseStyleSheet(css, rebase), origin, load, depth);
    }

    /// <summary>Adds the document's style elements and linked style sheets, in document order.</summary>
    public void AddDocument(HtmlDocument document, Func<string, string?>? load = null)
    {
        foreach (HtmlElement e in document.Descendants())
        {
            string? media = e.GetAttribute("media");
            if (media is not null && !MediaQuery.Matches(media, _media)) continue;
            if (e.Name == "style")
            {
                AddStyleSheet(e.TextContent, CssOrigin.Author, load);
            }
            else if (e.Name == "link" && load is not null && e.GetAttribute("href") is string href
                     && (e.GetAttribute("rel") ?? "").Split(' ').Any(r => r.Equals("stylesheet", StringComparison.OrdinalIgnoreCase)))
            {
                AddFetched(href, CssOrigin.Author, load, 0);
            }
        }
    }

    private void AddRules(List<CssRule> rules, CssOrigin origin, Func<string, string?>? load, int depth)
    {
        foreach (CssRule rule in rules)
        {
            switch (rule)
            {
                case CssImportRule import when load is not null && depth < MaxImportDepth && _imports < MaxImports && MediaQuery.Matches(import.Media, _media):
                    _imports++;
                    AddFetched(import.Url, origin, load, depth + 1);
                    break;
                case CssMediaRule media when MediaQuery.Matches(media.Query, _media):
                    AddRules(media.Rules, origin, load, depth);
                    break;
                case CssStyleRule style:
                    List<CssDeclaration> declarations = style.Declarations.SelectMany(d => CssProperties.Expand(d) ?? []).ToList();
                    if (declarations.Count == 0) break;
                    int order = _order++;
                    foreach (CssSelector selector in style.Selectors)
                    {
                        (selector.PseudoElement is null ? _elementRules : _pseudoElementRules).Add(new Entry(selector, declarations, origin, order));
                    }

                    break;
                case CssPageRule page:
                    PageRules.Add(page);
                    break;
                case CssFontFaceRule font:
                    FontFaces.Add(font);
                    break;
            }
        }
    }

    /// <summary>Rules bucketed by the subject's most selective part, so each element only tests likely rules.</summary>
    private sealed class RuleIndex
    {
        private readonly List<Entry> _universal = [];
        private readonly Dictionary<string, List<Entry>> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Entry>> _byClass = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Entry>> _byTag = new(StringComparer.Ordinal);

        public void Add(Entry entry)
        {
            CssCompound subject = entry.Selector.Compounds[0];
            if (subject.Conditions.OfType<IdCondition>().FirstOrDefault() is IdCondition id) Bucket(_byId, id.Id).Add(entry);
            else if (subject.Conditions.OfType<ClassCondition>().FirstOrDefault() is ClassCondition c) Bucket(_byClass, c.Name).Add(entry);
            else if (subject.Tag is string tag) Bucket(_byTag, tag).Add(entry);
            else _universal.Add(entry);
        }

        public IEnumerable<Entry> Candidates(HtmlElement e)
        {
            if (e.GetAttribute("id") is string id && _byId.TryGetValue(id, out List<Entry>? byId)) foreach (Entry x in byId) yield return x;
            foreach (string c in e.ClassList.Distinct())
            {
                if (_byClass.TryGetValue(c, out List<Entry>? byClass)) foreach (Entry x in byClass) yield return x;
            }

            if (_byTag.TryGetValue(e.Name, out List<Entry>? byTag)) foreach (Entry x in byTag) yield return x;
            foreach (Entry x in _universal) yield return x;
        }

        private static List<Entry> Bucket(Dictionary<string, List<Entry>> index, string key)
        {
            if (!index.TryGetValue(key, out List<Entry>? list)) index[key] = list = [];
            return list;
        }
    }

    private IEnumerable<Entry> Candidates(HtmlElement e, string? pseudoElement) =>
        (pseudoElement is null ? _elementRules : _pseudoElementRules).Candidates(e);

    private readonly Dictionary<string, List<CssDeclaration>> _inline = new(StringComparer.Ordinal);

    /// <summary>A style attribute's declarations, parsed once per distinct text: generated documents repeat the same few.</summary>
    private List<CssDeclaration> InlineDeclarations(string text)
    {
        if (!_inline.TryGetValue(text, out List<CssDeclaration>? declarations))
        {
            declarations = CssParser.ParseDeclarations(text).SelectMany(d => CssProperties.Expand(d) ?? []).ToList();
            if (_inline.Count < 10_000) _inline[text] = declarations;
        }

        return declarations;
    }

    /// <summary>Whether a rule targets this pseudo-element of the element: without one it generates nothing, and its style need not be computed.</summary>
    public bool HasRulesFor(HtmlElement element, string pseudoElement)
    {
        _ancestors.MoveTo(element.ParentElement);
        foreach (Entry entry in Candidates(element, pseudoElement))
        {
            if (entry.Selector.PseudoElement == pseudoElement && Matches(entry.Selector, element)) return true;
        }

        return false;
    }

    /// <summary>
    /// Matches a candidate rule's selector; the ancestor filter, already moved to the element's
    /// parent, rejects most descendant selectors before any walk up the tree. Every candidate
    /// counts as style work, rejected or not, so a style sheet of many rules for one element type
    /// is bounded too.
    /// </summary>
    private bool Matches(CssSelector selector, HtmlElement element)
    {
        _budget.Style(1);
        if (selector.AncestorKeys.Length > 0 && !_ancestors.MightContainAll(selector.AncestorKeys)) return false;
        return selector.Matches(element, _budget);
    }

    // The cascade's winners, one dictionary for every element: emptied, it lists what is added again in order.
    private readonly Dictionary<string, (CssDeclaration Declaration, long Rank)> _winners = new(StringComparer.Ordinal);

    /// <summary>Computes the style of an element, or of one of its pseudo-elements such as before.</summary>
    public ComputedStyle Compute(HtmlElement element, ComputedStyle? parent, string? pseudoElement = null)
    {
        Dictionary<string, (CssDeclaration Declaration, long Rank)> winners = _winners;
        winners.Clear();
        void Consider(CssDeclaration d, int tier, CssSpecificity s, int order)
        {
            static long Clamp(int x) => Math.Min(Math.Max(x, 0), 1023);
            long rank = ((long)tier << 60) | (Clamp(s.A) << 50) | (Clamp(s.B) << 40) | (Clamp(s.C) << 30) | (uint)order;
            if (!winners.TryGetValue(d.Property, out var current) || rank >= current.Rank) winners[d.Property] = (d, rank);
        }

        // Tiers, low to high: user agent, author, style attribute, then the same reversed for !important.
        _ancestors.MoveTo(element.ParentElement);
        foreach (Entry entry in Candidates(element, pseudoElement))
        {
            if (entry.Selector.PseudoElement != pseudoElement || !Matches(entry.Selector, element)) continue;
            foreach (CssDeclaration d in entry.Declarations)
            {
                int tier = entry.Origin == CssOrigin.UserAgent ? (d.Important ? 5 : 0) : (d.Important ? 3 : 1);
                Consider(d, tier, entry.Selector.Specificity, entry.Order);
            }
        }

        if (pseudoElement is null)
        {
            foreach (CssDeclaration d in PresentationalHints(element)) Consider(d, 1, default, 0);
            if (element.GetAttribute("style") is string inline)
            {
                foreach (CssDeclaration d in InlineDeclarations(inline)) Consider(d, d.Important ? 4 : 2, default, 0);
            }
        }

        var style = new ComputedStyle(parent, pseudoElement);
        if (element.GetAttribute("lang") is string language) style.Language = language;
        ResolveCustomProperties(style, parent, winners);

        Fill(style, parent, winners);
        if (parent is null && pseudoElement is null) _rootFontSize = style.FontSize;

        Blockify(style, element, parent);
        if (pseudoElement is null && element.Name == "table") LegacyAlignment(style);
        return style;
    }

    /// <summary>
    /// A table does not inherit the legacy alignment of an align attribute or a center element
    /// (-webkit-center and its kin): it goes back to start, as in Blink and Gecko. Email layouts
    /// center an outer cell and expect the text of the tables inside it to start on the left.
    /// </summary>
    private static void LegacyAlignment(ComputedStyle style)
    {
        if (style.Keyword("text-align") is "-webkit-center" or "-webkit-left" or "-webkit-right")
        {
            style.Values["text-align"] = [new CssValue(new CssToken(CssTokenType.Ident, "start"))];
        }
    }

    /// <summary>
    /// The style of a page or margin box from its declarations, in increasing precedence
    /// (CSS Paged Media 3, 7): the page context inherits from the root element.
    /// </summary>
    public ComputedStyle ComputeDeclarations(IEnumerable<CssDeclaration> declarations, ComputedStyle? parent)
    {
        var winners = new Dictionary<string, (CssDeclaration Declaration, long Rank)>(StringComparer.Ordinal);
        long order = 0;
        foreach (CssDeclaration d in declarations.SelectMany(d => CssProperties.Expand(d) ?? []))
        {
            long rank = ((d.Important ? 1L : 0L) << 40) | order++;
            if (!winners.TryGetValue(d.Property, out var current) || rank >= current.Rank) winners[d.Property] = (d, rank);
        }

        var style = new ComputedStyle(parent, null);
        ResolveCustomProperties(style, parent, winners);
        Fill(style, parent, winners);

        return style;
    }

    private readonly Dictionary<(ComputedStyle, string), ComputedStyle> _anonymous = [];

    /// <summary>The style of an anonymous box (CSS 2.1, 9.2.1.1): inherited values from the parent, the rest initial.</summary>
    public ComputedStyle Anonymous(ComputedStyle parent, string display)
    {
        if (_anonymous.TryGetValue((parent, display), out ComputedStyle? cached)) return cached;
        var style = new ComputedStyle(parent, null);
        var none = new Dictionary<string, (CssDeclaration Declaration, long Rank)>(StringComparer.Ordinal);
        Fill(style, parent, none);

        style.Values["display"] = [new CssValue(new CssToken(CssTokenType.Ident, display))];
        _anonymous[(parent, display)] = style;
        return style;
    }

    /// <summary>
    /// Gives every property its computed value. Undeclared properties take the parent's value or
    /// the initial one without going through the cascade, so styling stays cheap for documents
    /// with many elements; only declared properties are resolved one by one, font-size first since
    /// em units depend on it.
    /// </summary>
    private void Fill(ComputedStyle style, ComputedStyle? parent, Dictionary<string, (CssDeclaration Declaration, long Rank)> winners)
    {
        CssProperty[] all = CssProperties.ById;
        for (int i = 0; i < all.Length; i++) style.Values[i] = all[i].Inherited && parent is not null ? parent.Values[i] : _initial[i];
        style.FontSize = parent?.FontSize ?? 16;
        if (winners.ContainsKey("font-size")) Compute(style, parent, winners, "font-size");
        foreach (string property in winners.Keys)
        {
            if (property != "font-size" && CssProperties.All.ContainsKey(property)) Compute(style, parent, winners, property);
        }
    }

    private void Compute(ComputedStyle style, ComputedStyle? parent, Dictionary<string, (CssDeclaration Declaration, long Rank)> winners, string property)
    {
        CssProperty info = CssProperties.All[property];
        List<CssValue>? specified = null;
        string keyword = "unset";
        if (winners.TryGetValue(property, out var winner))
        {
            CssDeclaration d = winner.Declaration;
            List<CssValue>? value = d.Value;
            if (d.Shorthand is string shorthand)
            {
                List<CssValue>? substituted = Substitute(value, style.Custom);
                value = substituted is null ? null : CssProperties.ExpandShorthand(shorthand, substituted)?.FirstOrDefault(p => p.Property == property).Value;
            }
            else if (CssProperties.ContainsVar(value))
            {
                value = Substitute(value, style.Custom);
                if (value is not null && !CssProperties.IsWideKeyword(value, out _) && !info.Grammar.Matches(value)) value = null;
            }

            // A value that is invalid after substitution acts as unset.
            if (value is not null && !CssProperties.IsWideKeyword(value, out keyword)) specified = value;
            if (value is null) keyword = "unset";
        }

        if (specified is null)
        {
            bool inherit = keyword == "inherit" || (keyword is "unset" or "revert" or "revert-layer" && info.Inherited);
            if (inherit && parent is not null)
            {
                style.Values[property] = parent.Values[property];
                if (property == "font-size") style.FontSize = parent.FontSize;
                return;
            }

            specified = info.Initial;
        }

        style.Values[property] = ComputeValue(style, parent, property, specified);
    }

    private List<CssValue> ComputeValue(ComputedStyle style, ComputedStyle? parent, string property, List<CssValue> value)
    {
        double parentSize = parent?.FontSize ?? 16;
        switch (property)
        {
            case "font-size":
                double size = FontSize(value, parentSize);
                style.FontSize = double.IsNaN(size) ? parentSize : Math.Max(0, Math.Min(MaxFontSize, size));
                return [Px(style.FontSize)];
            case "font-weight":
                return [Number(FontWeight(value, parent is null ? 400 : parent.Number("font-weight")))];
            case "line-height":
                if (value.Count == 1 && value[0].IsToken && value[0].Token.Type == CssTokenType.Percentage) return [Px(value[0].Token.Number / 100 * style.FontSize)];
                if (value.Count == 1 && value[0].IsToken && value[0].Token.Type == CssTokenType.Number && !(Math.Abs(value[0].Token.Number) <= MaxLineHeight))
                {
                    return [Number(double.IsNaN(value[0].Token.Number) ? 1.2 : Math.Max(0, Math.Min(MaxLineHeight, value[0].Token.Number)))];
                }

                break;
            case "color":
                if (value.Count == 1 && value[0].IsIdent("currentcolor")) return parent?.Values["color"] ?? [new CssValue(new CssToken(CssTokenType.Ident, "black"))];
                break;
        }

        return Absolutize(value, style.FontSize);
    }

    private static readonly Dictionary<string, double> SizeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xx-small"] = 9, ["x-small"] = 10, ["small"] = 13, ["medium"] = 16,
        ["large"] = 18, ["x-large"] = 24, ["xx-large"] = 32, ["xxx-large"] = 48,
    };

    private double FontSize(List<CssValue> value, double parentSize)
    {
        if (value.Count != 1) return parentSize;
        CssValue v = value[0];
        if (v.IsToken && v.Token.Type == CssTokenType.Ident)
        {
            string k = v.Token.Value.ToLowerInvariant();
            return k switch
            {
                "larger" => parentSize * 1.2,
                "smaller" => parentSize / 1.2,
                _ => SizeKeywords.TryGetValue(k, out double size) ? size : parentSize,
            };
        }

        if (v.IsToken && v.Token.Type == CssTokenType.Percentage) return Math.Max(0, v.Token.Number / 100 * parentSize);

        // Lengths in font-size use the parent's font size for em.
        CssValue absolute = Absolutize(value, parentSize)[0];
        if (CssGrammar.IsMath(absolute)) return Math.Max(0, CssMath.Evaluate(absolute, parentSize, out _) ?? parentSize);
        return absolute.IsToken && absolute.Token.Type == CssTokenType.Dimension && absolute.Token.Unit == "px" ? Math.Max(0, absolute.Token.Number) : parentSize;
    }

    private static double FontWeight(List<CssValue> value, double parentWeight)
    {
        if (value.Count != 1 || !value[0].IsToken) return 400;
        CssToken t = value[0].Token;
        if (t.Type == CssTokenType.Number) return double.IsNaN(t.Number) ? 400 : Math.Min(1000, Math.Max(1, t.Number));
        return t.Value.ToLowerInvariant() switch
        {
            "bold" => 700,
            "bolder" => parentWeight < 350 ? 400 : parentWeight < 550 ? 700 : 900,
            "lighter" => parentWeight < 550 ? 100 : parentWeight < 750 ? 400 : 700,
            _ => 400,
        };
    }

    /// <summary>Converts every length to px and evaluates math that has no percentages.</summary>
    private List<CssValue> Absolutize(List<CssValue> value, double em)
    {
        List<CssValue>? result = null;
        for (int i = 0; i < value.Count; i++)
        {
            CssValue v = value[i];
            CssValue converted = v;
            if (v.IsToken)
            {
                if (v.Token.Type == CssTokenType.Dimension && ToPx(v.Token, em) is double px) converted = Px(px);
                else if (v.Token.Type is CssTokenType.Dimension or CssTokenType.Percentage && !(Math.Abs(v.Token.Number) <= MaxLength) && IsLengthOrPercentage(v.Token)) converted = Clamped(v.Token);
            }
            else if (v.Function is not ("url" or "var"))
            {
                List<CssValue> children = Absolutize(v.Children, em);
                converted = ReferenceEquals(children, v.Children) ? v : new CssValue(v.Block!.Value, v.Function, children);
                if (CssGrammar.IsMath(converted) && CssMath.Evaluate(converted, null, out bool isLength) is double n)
                {
                    converted = isLength ? Px(n) : Number(n);
                }
            }

            if (!ReferenceEquals(converted, v))
            {
                result ??= [.. value];
                result[i] = converted;
            }
        }

        return result ?? value;
    }

    private double? ToPx(CssToken t, double em) => t.Unit switch
    {
        "px" => null,
        "cm" => t.Number * 96 / 2.54,
        "mm" => t.Number * 96 / 25.4,
        "q" => t.Number * 96 / 101.6,
        "in" => t.Number * 96,
        "pt" => t.Number * 96 / 72,
        "pc" => t.Number * 16,
        "em" => t.Number * em,
        "rem" => t.Number * _rootFontSize,
        "ex" or "ch" => t.Number * em / 2,
        "rex" or "rch" => t.Number * _rootFontSize / 2,
        "cap" => t.Number * em * 0.7,
        "ic" => t.Number * em,
        "lh" => t.Number * em * 1.2,
        "rlh" => t.Number * _rootFontSize * 1.2,
        "vw" or "vi" or "svw" or "lvw" or "dvw" => t.Number * _media.Width / 100,
        "vh" or "vb" or "svh" or "lvh" or "dvh" => t.Number * _media.Height / 100,
        "vmin" => t.Number * Math.Min(_media.Width, _media.Height) / 100,
        "vmax" => t.Number * Math.Max(_media.Width, _media.Height) / 100,
        _ => null,
    };

    /// <summary>A length in px, clamped to <see cref="MaxLength"/> either way; NaN is 0.</summary>
    private static CssValue Px(double px) => new(new CssToken(CssTokenType.Dimension, Number: ClampLength(px), Unit: "px"));

    internal static double ClampLength(double px) => double.IsNaN(px) ? 0 : Math.Max(-MaxLength, Math.Min(MaxLength, px));

    private static bool IsLengthOrPercentage(CssToken t) => t.Type == CssTokenType.Percentage || t.Unit == "px";

    /// <summary>A px length or a percentage past <see cref="MaxLength"/>, brought back to it.</summary>
    private static CssValue Clamped(CssToken t) => new(t with { Number = ClampLength(t.Number) });

    private static CssValue Number(double n) => new(new CssToken(CssTokenType.Number, Number: n, IsInteger: n == Math.Floor(n)));

    // Floats, absolute positioning, the root and flex or grid items become block-level (CSS Display, 2.7).
    private static void Blockify(ComputedStyle style, HtmlElement element, ComputedStyle? parent)
    {
        string display = style.Keywords("display");
        string position = style.Keyword("position");
        string parentDisplay = parent?.Keywords("display") ?? "";
        bool blockify = (parent is null && style.PseudoElement is null)
            || style.Keyword("float") != "none"
            || position is "absolute" or "fixed"
            || parentDisplay.Contains("flex") || parentDisplay.Contains("grid");
        if (!blockify || display == "contents") return;

        string? block = display switch
        {
            "inline" or "inline-block" or "run-in" or "table-row" or "table-cell" or "table-row-group" or "table-header-group"
                or "table-footer-group" or "table-column" or "table-column-group" or "table-caption" => "block",
            "inline-table" => "table",
            "inline-flex" => "flex",
            "inline-grid" => "grid",
            "inline list-item" or "inline flow list-item" => "list-item",
            _ => null,
        };
        if (block is not null) style.Values["display"] = [new CssValue(new CssToken(CssTokenType.Ident, block))];
    }

    private void ResolveCustomProperties(ComputedStyle style, ComputedStyle? parent, Dictionary<string, (CssDeclaration Declaration, long Rank)> winners)
    {
        Dictionary<string, List<CssValue>>? own = null;
        foreach (KeyValuePair<string, (CssDeclaration Declaration, long Rank)> w in winners)
        {
            if (CssProperties.IsCustom(w.Key)) (own ??= new(StringComparer.Ordinal))[w.Key] = w.Value.Declaration.Value;
        }

        if (own is null) return;

        // The parent's map is shared, not copied: each property set here adds one path to it.
        CustomProperties inherited = parent?.Custom ?? CustomProperties.Empty;
        CustomProperties custom = inherited;
        var resolving = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        List<CssValue>? Resolve(string name, int depth)
        {
            if (done.Contains(name)) return custom.Find(name);
            if (!own.TryGetValue(name, out List<CssValue>? raw)) return inherited.Find(name);
            if (!resolving.Add(name) || depth > MaxVarDepth) return null; // a cycle makes the property invalid

            List<CssValue>? value;
            if (CssProperties.IsWideKeyword(raw, out string keyword))
            {
                value = keyword is "initial" ? null : inherited.Find(name);
            }
            else
            {
                value = Substitute(raw, n => Resolve(n, depth + 1));
            }

            resolving.Remove(name);
            done.Add(name);
            custom = custom.With(name, value);
            return value;
        }

        foreach (string name in own.Keys) Resolve(name, 0);
        style.Custom = custom;
    }

    private List<CssValue>? Substitute(List<CssValue> value, CustomProperties custom) => Substitute(value, custom.Find);

    /// <summary>
    /// Replaces var() references; null when one cannot be resolved and has no fallback. Every
    /// value copied counts as style work: one declaration may expand to 50,000 values, and a rule
    /// can give it to every element.
    /// </summary>
    private List<CssValue>? Substitute(List<CssValue> value, Func<string, List<CssValue>?> lookup)
    {
        int budget = MaxVarOutput;
        List<CssValue>? result = Substitute(value, lookup, ref budget);
        _budget.Style(MaxVarOutput - Math.Max(0, budget));
        return result is null ? null : CssProperties.Trim(result);
    }

    private static List<CssValue>? Substitute(List<CssValue> value, Func<string, List<CssValue>?> lookup, ref int budget)
    {
        var result = new List<CssValue>();
        foreach (CssValue v in value)
        {
            if (v.Function == "var")
            {
                int i = 0;
                CssSelector.SkipWhitespace(v.Children, ref i);
                if (i >= v.Children.Count || !v.Children[i].IsToken || !CssProperties.IsCustom(v.Children[i].Token.Value)) return null;
                string name = v.Children[i].Token.Value;
                List<CssValue>? replacement = lookup(name);
                if (replacement is null)
                {
                    int comma = v.Children.FindIndex(c => c.IsToken && c.Token.Type == CssTokenType.Comma);
                    if (comma < 0) return null;
                    replacement = Substitute(v.Children.Skip(comma + 1).ToList(), lookup, ref budget);
                    if (replacement is null) return null;
                    replacement = CssProperties.Trim(replacement);
                }

                budget -= replacement.Count;
                if (budget < 0) return null;
                result.AddRange(replacement);
            }
            else if (!v.IsToken && CssProperties.ContainsVar(v.Children))
            {
                List<CssValue>? children = Substitute(v.Children, lookup, ref budget);
                if (children is null) return null;
                result.Add(new CssValue(v.Block!.Value, v.Function, children));
            }
            else
            {
                if (--budget < 0) return null;
                result.Add(v);
            }
        }

        return result;
    }

    /// <summary>HTML attributes that map to CSS (HTML, 15.3 and following), as author rules of no specificity.</summary>
    private static IEnumerable<CssDeclaration> PresentationalHints(HtmlElement e)
    {
        var css = new System.Text.StringBuilder();
        string? Attr(string name) => e.GetAttribute(name)?.Trim();

        if (Attr("align") is string align)
        {
            string a = align.ToLowerInvariant();
            if (e.Name is "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "caption" or "td" or "th" or "tr" or "thead" or "tbody" or "tfoot" or "legend")
            {
                // The legacy values browsers map these to: they align like center, left and right,
                // but a nested table drops them (see LegacyAlignment), as email layouts expect.
                string? value = a switch
                {
                    "center" or "middle" => "-webkit-center",
                    "left" => "-webkit-left",
                    "right" => "-webkit-right",
                    "justify" => "justify",
                    "absmiddle" => "center",
                    _ => null,
                };
                if (value is not null) css.Append("text-align:").Append(value).Append(';');
            }
            else if (e.Name == "table" && a == "center") css.Append("margin-left:auto;margin-right:auto;");
            else if (e.Name is "table" or "img" or "object" or "iframe" && a is "left" or "right") css.Append("float:").Append(a).Append(';');
            else if (e.Name is "img" or "object" or "iframe" && a is "top" or "middle" or "bottom") css.Append("vertical-align:").Append(a == "middle" ? "middle" : a).Append(';');
        }

        if (Attr("valign") is string valign && e.Name is "td" or "th" or "tr" or "thead" or "tbody" or "tfoot" or "col" or "colgroup")
        {
            string v = valign.ToLowerInvariant();
            if (v is "top" or "middle" or "bottom" or "baseline") css.Append("vertical-align:").Append(v).Append(';');
        }

        if (e.Name is "table" or "td" or "th" or "col" or "colgroup" or "img" or "svg" or "hr" or "iframe" or "video" or "canvas" or "object" or "embed" or "input"
            && LegacyLength(Attr("width")) is string width)
        {
            css.Append("width:").Append(width).Append(';');
        }

        if (e.Name is "table" or "td" or "th" or "tr" or "img" or "svg" or "iframe" or "video" or "canvas" or "object" or "embed"
            && LegacyLength(Attr("height")) is string height)
        {
            css.Append("height:").Append(height).Append(';');
        }

        if (e.Name is "body" or "table" or "tr" or "td" or "th" or "thead" or "tbody" or "tfoot" && LegacyColor(Attr("bgcolor")) is string bg)
        {
            css.Append("background-color:").Append(bg).Append(';');
        }

        if (e.Name == "body" && LegacyColor(Attr("text")) is string text) css.Append("color:").Append(text).Append(';');

        if (e.Name == "font")
        {
            if (LegacyColor(Attr("color")) is string color) css.Append("color:").Append(color).Append(';');
            if (Attr("face") is string face && face.Length > 0) css.Append("font-family:").Append(string.Join(",", face.Split(',').Select(f => "\"" + f.Trim().Replace("\"", "") + "\""))).Append(';');
            if (FontSizeAttribute(Attr("size")) is string size) css.Append("font-size:").Append(size).Append(';');
        }

        if (e.Name == "table")
        {
            if (LegacyPixels(Attr("border")) is int border && border > 0) css.Append("border:").Append(border).Append("px outset gray;");
            else if (Attr("border") is "") css.Append("border:1px outset gray;");
            if (LegacyPixels(Attr("cellspacing")) is int spacing) css.Append("border-spacing:").Append(spacing).Append("px;");
        }

        if (e.Name is "td" or "th")
        {
            if (Attr("nowrap") is not null) css.Append("white-space:nowrap;");
            HtmlElement? table = e.ParentElement;
            while (table is not null && table.Name != "table") table = table.ParentElement;
            if (table is not null)
            {
                if (LegacyPixels(table.GetAttribute("cellpadding")) is int padding) css.Append("padding:").Append(padding).Append("px;");
                string? tableBorder = table.GetAttribute("border");
                if (tableBorder is "" || LegacyPixels(tableBorder) > 0) css.Append("border:1px inset gray;");
            }
        }

        if (e.Name is "ul" or "ol" or "li" && Attr("type") is string listType)
        {
            string? mapped = listType switch
            {
                "1" => "decimal",
                "a" => "lower-alpha",
                "A" => "upper-alpha",
                "i" => "lower-roman",
                "I" => "upper-roman",
                _ => listType.ToLowerInvariant() is "disc" or "circle" or "square" ? listType.ToLowerInvariant() : null,
            };
            if (mapped is not null) css.Append("list-style-type:").Append(mapped).Append(';');
        }

        return css.Length == 0 ? [] : CssParser.ParseDeclarations(css.ToString()).SelectMany(d => CssProperties.Expand(d) ?? []);
    }

#pragma warning disable CA1846 // .NET Standard 2.0 has no span overloads for parsing.

    /// <summary>A legacy dimension: digits, optionally a fraction, optionally %.</summary>
    private static string? LegacyLength(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        int i = 0;
        while (i < value!.Length && (char.IsDigit(value[i]) || value[i] == '.')) i++;
        if (i == 0 || !double.TryParse(value.Substring(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return null;
        bool percent = i < value.Length && value[i] == '%';
        return n.ToString(CultureInfo.InvariantCulture) + (percent ? "%" : "px");
    }

    private static int? LegacyPixels(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        int i = 0;
        while (i < value!.Length && char.IsDigit(value[i])) i++;
        return i > 0 && int.TryParse(value.Substring(0, Math.Min(i, 6)), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static string? LegacyColor(string? value)
    {
        if (string.IsNullOrEmpty(value) || value!.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;
        if (CssColor.Parse(value) is not null) return value;
        string hex = value.TrimStart('#');
        return hex.Length is 3 or 6 && hex.All(Uri.IsHexDigit) ? "#" + hex : null;
    }

    private static string? FontSizeAttribute(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        string v = value!;
        int sign = v[0] == '+' ? 1 : v[0] == '-' ? -1 : 0;
        if (!int.TryParse(sign == 0 ? v : v.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return null;
        int size = Math.Min(7, Math.Max(1, sign == 0 ? n : 3 + (sign * n)));
        return size switch { 1 => "x-small", 2 => "small", 3 => "medium", 4 => "large", 5 => "x-large", 6 => "xx-large", _ => "xxx-large" };
    }

#pragma warning restore CA1846
}
