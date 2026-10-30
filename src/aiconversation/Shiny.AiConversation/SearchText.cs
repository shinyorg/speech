namespace Shiny.AiConversation;

/// <summary>
/// Helpers for <see cref="IAiSessionStore.SearchMessages"/> implementations.
/// </summary>
public static class SearchText
{
    static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "did", "do", "does", "for", "from", "had", "has", "have",
        "how", "i", "if", "in", "is", "it", "its", "me", "my", "of", "on", "or", "that", "the", "their", "them", "then",
        "there", "they", "this", "to", "was", "we", "were", "what", "when", "where", "which", "who", "why", "with", "you", "your"
    };

    /// <summary>
    /// The query split into search terms: punctuation, possessives and common stop words removed.
    /// </summary>
    public static string[] Terms(string query)
        => Words(query)
            .Select(w => w.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ? w[..^2] : w)
            .Select(w => w.Replace("'", ""))
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// True when a word in <paramref name="text"/> starts with <paramref name="term"/> ("plumber" matches "plumbers").
    /// </summary>
    public static bool ContainsTerm(string text, string term)
        => Words(text).Any(w => w.StartsWith(term, StringComparison.OrdinalIgnoreCase));

    static IEnumerable<string> Words(string text)
        => text.Split(
            [' ', '\t', '\r', '\n', ',', '.', '?', '!', ';', ':', '"', '(', ')', '[', ']', '-', '/', '’'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

    /// <summary>
    /// A short excerpt of <paramref name="text"/> around the first matching term.
    /// </summary>
    public static string Snippet(string text, IReadOnlyList<string> terms, int radius = 80)
    {
        var index = terms
            .Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var start = Math.Max(0, index - radius);
        var end = Math.Min(text.Length, index + radius);
        var snippet = text[start..end];

        if (start > 0)
            snippet = "…" + snippet;

        if (end < text.Length)
            snippet += "…";

        return snippet;
    }
}
