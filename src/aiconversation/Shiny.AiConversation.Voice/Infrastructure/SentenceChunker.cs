using System.Text;
using System.Text.RegularExpressions;

namespace Shiny.AiConversation.Voice.Infrastructure;

/// <summary>
/// Splits a streamed reply into sentences so speech can start before the reply is finished. A sentence ends
/// at <c>. ! ? 。！？</c> followed by whitespace, or at a line break. Decimals ("3.14"), list numbers ("1. ")
/// and common abbreviations ("e.g. ", "Dr. ") don't end a sentence. Fenced code blocks are dropped.
/// </summary>
sealed class SentenceChunker
{
    static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "st", "jr", "sr", "prof", "vs", "etc", "inc", "ltd", "co", "no", "approx", "e.g", "i.e", "a.m", "p.m"
    };

    readonly StringBuilder buffer = new();
    bool inFence;
    bool atLineStart = true;


    /// <summary>
    /// Feeds streamed text; returns the sentences it completed.
    /// </summary>
    public List<string> Add(string text)
    {
        var done = new List<string>();
        foreach (var c in text)
            this.Push(c, done);

        return done;
    }


    /// <summary>
    /// The end of the reply: returns whatever is left.
    /// </summary>
    public List<string> Flush()
    {
        var done = new List<string>();
        this.Emit(done);
        this.inFence = false;
        this.atLineStart = true;
        return done;
    }


    void Push(char c, List<string> done)
    {
        // a line starting with ``` toggles a code fence; fenced content is never spoken
        if (this.atLineStart && c == '`')
        {
            this.buffer.Append(c);
            if (this.buffer.ToString().EndsWith("```", StringComparison.Ordinal) && LineIsOnlyBackticks(this.buffer))
            {
                RemoveTrailing(this.buffer, 3);
                this.inFence = !this.inFence;
                this.Emit(done);
            }
            return;
        }

        if (c == '\n')
        {
            this.atLineStart = true;
            if (this.inFence)
                this.buffer.Clear();
            else
                this.Emit(done);
            return;
        }

        if (this.inFence)
        {
            this.atLineStart = false;
            return;
        }

        if (Char.IsWhiteSpace(c) && this.buffer.Length > 0 && IsSentenceEnd(this.buffer))
        {
            this.Emit(done);
            return;
        }

        this.atLineStart = this.atLineStart && Char.IsWhiteSpace(c);
        this.buffer.Append(c);
    }


    void Emit(List<string> done)
    {
        var sentence = this.buffer.ToString().Trim();
        this.buffer.Clear();
        if (sentence.Length > 0 && !this.inFence)
            done.Add(sentence);
    }


    static bool IsSentenceEnd(StringBuilder sb)
    {
        var text = sb.ToString().TrimEnd('"', '\'', ')', ']', '”', '’', '*', '_');
        if (text.Length == 0)
            return false;

        var last = text[^1];
        if (last is '!' or '?' or '。' or '！' or '？')
            return true;

        if (last != '.')
            return false;

        // the word before the dot: list numbers ("1."), abbreviations ("e.g."), initials ("J.")
        var start = text.LastIndexOfAny([' ', '\t', '(']) + 1;
        var word = text[start..^1].TrimStart('*', '_', '"', '\'');
        if (word.Length == 0)
            return true;

        if (word.All(Char.IsDigit))
            return false;

        if (Abbreviations.Contains(word) || (word.Length == 1 && Char.IsUpper(word[0])))
            return false;

        return true;
    }


    static bool LineIsOnlyBackticks(StringBuilder sb)
    {
        var text = sb.ToString();
        var lineStart = text.LastIndexOf('\n') + 1;
        return text[lineStart..].Trim().All(ch => ch == '`');
    }


    static void RemoveTrailing(StringBuilder sb, int count)
        => sb.Length = Math.Max(0, sb.Length - count);
}


/// <summary>
/// Turns a markdown-ish sentence into something a speech engine reads naturally.
/// </summary>
static partial class SpeechText
{
    public static string StripMarkdown(string text)
    {
        var s = text;
        s = ImageRegex().Replace(s, "");                 // ![alt](url)
        s = LinkRegex().Replace(s, "$1");                // [text](url) -> text
        s = HeadingRegex().Replace(s, "");               // ## Heading
        s = ListMarkerRegex().Replace(s, "");            // - item, * item, 1. item, > quote
        s = InlineCodeRegex().Replace(s, "$1");          // `code` -> code
        s = EmphasisRegex().Replace(s, "$2");            // **bold**, *italic*, __bold__, _italic_, ~~strike~~
        s = s.Replace("|", " ");                         // table pipes
        s = HorizontalRuleRegex().Replace(s, "");
        s = WhitespaceRegex().Replace(s, " ");
        return s.Trim();
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^\s*#{1,6}\s+")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(?:[-*+>]|\d+[.)])\s+")]
    private static partial Regex ListMarkerRegex();

    [GeneratedRegex(@"`([^`]*)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"(\*\*|__|\*|_|~~)(.+?)\1")]
    private static partial Regex EmphasisRegex();

    [GeneratedRegex(@"^\s*([-*_]\s*){3,}$")]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
