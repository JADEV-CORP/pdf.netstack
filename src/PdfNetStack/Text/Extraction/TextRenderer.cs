using System.Text;

namespace PdfNetStack.Text.Extraction;

/// <summary>
/// Writes a block as plain text and as Markdown (CommonMark, with GitHub's pipe tables).
/// </summary>
/// <remarks>
/// Markdown escapes only what would change the structure or let a document's text act as markup:
/// a line start that reads as a heading, quote, list or rule, brackets (which could forge a
/// link), angle brackets (raw HTML) and, in tables, the pipe. Emphasis characters are left alone,
/// so identifiers such as snake_case stay readable. Links come from the page's link annotations
/// and only for http, https and mailto targets.
/// </remarks>
internal static class TextRenderer
{
    public static PdfTextBlock Render(TextBlock block, IReadOnlyDictionary<PdfWord, string> links)
    {
        if (block.Rows is not null) return RenderTable(block, links);
        string text = string.Join("\n", block.Lines.Select(line => string.Join(" ", line.Select(w => w.Text))));
        string markdown = block.Kind switch
        {
            PdfTextBlockKind.Heading => new string('#', Math.Min(6, Math.Max(1, block.Level))) + " " + Inline(Flatten(block.Lines), links, lineStart: false),
            PdfTextBlockKind.ListItem => ListItem(block, links),
            _ => Inline(Flatten(block.Lines), links, lineStart: true),
        };

        return new PdfTextBlock(block.Kind, block.Level, text, markdown, block.Bounds(), []);
    }

    private static List<PdfWord> Flatten(List<List<PdfWord>> lines) => [.. lines.SelectMany(l => l)];

    /// <summary>A list item: its bullet becomes "- ", a number label stays, indented two spaces per level below the first.</summary>
    private static string ListItem(TextBlock block, IReadOnlyDictionary<PdfWord, string> links)
    {
        List<PdfWord> words = Flatten(block.Lines);
        string indent = new(' ', 2 * Math.Max(0, block.Level - 1));
        if (words.Count == 0) return indent + "-";
        string label = "-";
        var line = new List<PdfWord>(words);
        if (LayoutBlocks.IsBullet(block.Lines[0]))
        {
            if (words[0].Text.Length == 1)
            {
                line.RemoveAt(0);
            }
            else
            {
                // "•Item": the word without its bullet, as a word of its own.
                string rest = Inline([words[0]], links, lineStart: false).Substring(1);
                return indent + "- " + rest + (words.Count > 1 ? " " + Inline(words.GetRange(1, words.Count - 1), links, lineStart: false) : "");
            }
        }
        else if (LayoutBlocks.IsNumberLabel(block.Lines[0]))
        {
            string first = words[0].Text;
            bool numeric = first.Length is >= 2 and <= 4 && first[^1] is '.' or ')' && first.Substring(0, first.Length - 1).All(char.IsDigit);
            label = numeric ? first : "- " + Escape(first, lineStart: false);
            line.RemoveAt(0);
        }

        return indent + label + (line.Count > 0 ? " " + Inline(line, links, lineStart: false) : "");
    }

    private static PdfTextBlock RenderTable(TextBlock block, IReadOnlyDictionary<PdfWord, string> links)
    {
        List<List<List<List<PdfWord>>>> rows = block.Rows!;
        var cells = rows.Select(row => row.Select(cell => string.Join(" ", cell.SelectMany(l => l).Select(w => w.Text))).ToList()).ToList();
        string text = string.Join("\n", cells.Select(row => string.Join("\t", row)));

        int columns = rows.Max(r => r.Count);
        var md = new StringBuilder();
        for (int r = 0; r < rows.Count; r++)
        {
            md.Append('|');
            for (int c = 0; c < columns; c++)
            {
                string cell = c < rows[r].Count ? Inline([.. rows[r][c].SelectMany(l => l)], links, lineStart: false, table: true) : "";
                md.Append(' ').Append(cell).Append(cell.Length > 0 ? " |" : "|");
            }

            md.Append('\n');
            if (r == 0)
            {
                md.Append('|');
                for (int c = 0; c < columns; c++) md.Append(" --- |");
                md.Append('\n');
            }
        }

        return new PdfTextBlock(PdfTextBlockKind.Table, 0, text, md.ToString().TrimEnd('\n'), block.Bounds(), cells.Select(r => (IReadOnlyList<string>)r).ToList());
    }

    /// <summary>Words as one line of Markdown, runs of words under one link as [text](target).</summary>
    private static string Inline(List<PdfWord> words, IReadOnlyDictionary<PdfWord, string> links, bool lineStart, bool table = false)
    {
        var sb = new StringBuilder();
        string? open = null;
        for (int i = 0; i < words.Count; i++)
        {
            links.TryGetValue(words[i], out string? target);
            if (target != open)
            {
                if (open is not null) sb.Append("](").Append(open).Append(')');
                if (i > 0) sb.Append(' ');
                if (target is not null) sb.Append('[');
                open = target;
            }
            else if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Escape(words[i].Text, lineStart && i == 0, table));
        }

        if (open is not null) sb.Append("](").Append(open).Append(')');
        return sb.ToString();
    }

    /// <summary>Escapes what would act as Markdown in a word; <paramref name="lineStart"/> for the first word of a block.</summary>
    internal static string Escape(string text, bool lineStart, bool table = false)
    {
        var sb = new StringBuilder(text.Length + 4);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\n' or '\r')
            {
                sb.Append(' ');
                continue;
            }

            // A backslash only escapes punctuation, so only one before punctuation needs its own.
            bool escape = c switch
            {
                '[' or ']' or '<' => true,
                '\\' => i + 1 < text.Length && char.IsPunctuation(text[i + 1]),
                '|' => table,
                _ => false,
            };

            if (escape) sb.Append('\\');
            sb.Append(c);
        }

        if (lineStart && sb.Length > 0)
        {
            // A block that starts like a heading, quote, list item, rule or setext underline stays text.
            char first = sb[0];
            int digits = 0;
            while (digits < sb.Length && char.IsDigit(sb[digits])) digits++;
            if (first is '#' or '>' or '-' or '+' or '*' or '=' or '_' or '~' or '`' or '|')
            {
                sb.Insert(0, '\\');
            }
            else if (digits > 0 && digits + 1 == sb.Length && sb[digits] is '.' or ')')
            {
                sb.Insert(digits, '\\');
            }
        }

        return sb.ToString();
    }

    /// <summary>A link target fit for Markdown: http, https or mailto, with characters that would end it percent encoded; else null.</summary>
    public static string? LinkTarget(string? uri)
    {
        if (uri is null) return null;
        uri = uri.Trim();
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return null;
        var sb = new StringBuilder(uri.Length);
        foreach (char c in uri)
        {
            switch (c)
            {
                case ' ': sb.Append("%20"); break;
                case '(': sb.Append("%28"); break;
                case ')': sb.Append("%29"); break;
                case '<': sb.Append("%3C"); break;
                case '>': sb.Append("%3E"); break;
                case '[': sb.Append("%5B"); break;
                case ']': sb.Append("%5D"); break;
                default:
                    if (char.IsControl(c)) continue;
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
