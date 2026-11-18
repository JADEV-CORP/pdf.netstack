using System.Text;
using System.Text.RegularExpressions;

namespace PdfNetStack.TextBench;

/// <summary>
/// Word and order accuracy of extracted text against the truth. Words are runs of letters and
/// digits, compared after NFKC normalization and lower casing, so punctuation, spacing, bullets
/// and Markdown syntax count for nothing.
/// </summary>
internal static partial class Metrics
{
    public static List<string> Words(string text) =>
        [.. WordRegex().Matches(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant()).Select(m => m.Value)];

    /// <summary>Words of <paramref name="truth"/> also found in <paramref name="output"/>, counted as multisets.</summary>
    public static int Matched(List<string> truth, List<string> output)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string w in output) counts[w] = counts.GetValueOrDefault(w) + 1;
        int matched = 0;
        foreach (string w in truth)
        {
            if (counts.GetValueOrDefault(w) <= 0) continue;
            counts[w]--;
            matched++;
        }

        return matched;
    }

    /// <summary>
    /// The longest common subsequence of the two word sequences: the truth's words the output has
    /// in the truth's order. Divided by the truth's length it is the order accuracy, which a word
    /// missing or out of place lowers.
    /// </summary>
    public static int InOrder(List<string> truth, List<string> output)
    {
        // One row of the dynamic programme at a time: O(n m) time, O(m) memory.
        var previous = new int[output.Count + 1];
        var current = new int[output.Count + 1];
        foreach (string t in truth)
        {
            for (int j = 1; j <= output.Count; j++)
            {
                current[j] = string.Equals(t, output[j - 1], StringComparison.Ordinal) ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
        }

        return previous[output.Count];
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
