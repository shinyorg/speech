using Microsoft.Extensions.AI;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// Collects usage per model request. In a streamed tool loop each request reports its own
/// <see cref="UsageContent"/> (the function-invoking client does not sum them while streaming), so the
/// last one is the true size of what the provider last saw.
/// </summary>
sealed class UsageAccumulator
{
    long? input, output, cached, reasoning, total;
    UsageDetails? last;

    public int Requests { get; private set; }

    /// <summary>
    /// Input + output of the last request; null when the provider reported nothing.
    /// </summary>
    public long? LastRequestTokens => this.last switch
    {
        null => null,
        { InputTokenCount: null, OutputTokenCount: null } => this.last.TotalTokenCount,
        var l => (l.InputTokenCount ?? 0) + (l.OutputTokenCount ?? 0)
    };

    public void Add(UsageDetails details)
    {
        this.Requests++;
        this.last = details;
        this.input = Sum(this.input, details.InputTokenCount);
        this.output = Sum(this.output, details.OutputTokenCount);
        this.cached = Sum(this.cached, details.CachedInputTokenCount);
        this.reasoning = Sum(this.reasoning, details.ReasoningTokenCount);
        this.total = Sum(this.total, details.TotalTokenCount ?? (details.InputTokenCount ?? 0) + (details.OutputTokenCount ?? 0));
    }

    public UsageSnapshot? ToSnapshot() => this.Requests == 0
        ? null
        : new UsageSnapshot(this.input, this.output, this.cached, this.reasoning, this.total);

    public SessionUsage AddTo(SessionUsage usage) => new(
        usage.InputTokens + (this.input ?? 0),
        usage.OutputTokens + (this.output ?? 0),
        usage.CachedInputTokens + (this.cached ?? 0),
        usage.ReasoningTokens + (this.reasoning ?? 0),
        usage.TotalTokens + (this.total ?? 0),
        // a provider that reports no usage still made (at least) one request
        usage.RequestCount + Math.Max(1, this.Requests)
    );

    static long? Sum(long? a, long? b) => a == null && b == null ? null : (a ?? 0) + (b ?? 0);
}


/// <summary>
/// The last-resort "expects a reply" signal (L8) for models that never call <c>ask_user</c>.
/// </summary>
static class QuestionHeuristic
{
    public const string QuestionId = "reply";
    static readonly char[] SentenceEnd = ['.', '!', '?', '\n', '。', '！', '？'];

    /// <summary>
    /// True when the text ends in a question, ignoring closing markup and terminator runs ("Really?!").
    /// </summary>
    public static bool EndsWithQuestion(string? text)
    {
        if (String.IsNullOrWhiteSpace(text))
            return false;

        var i = TrimClosing(text);
        var hasQuestion = false;
        while (i >= 0 && IsTerminator(text[i]))
        {
            if (text[i] is '?' or '？')
                hasQuestion = true;
            i--;
        }
        return hasQuestion;
    }

    public static string LastQuestion(string text)
    {
        var end = TrimClosing(text);
        var i = end;
        while (i >= 0 && IsTerminator(text[i]))
            i--;

        var start = i >= 0 ? text.LastIndexOfAny(SentenceEnd, i) : -1;
        return text[(start + 1)..(end + 1)].Trim().TrimStart('*', '_', '-', ' ');
    }

    static int TrimClosing(string text)
    {
        var i = text.Length - 1;
        while (i >= 0 && IsClosingMarkup(text[i]))
            i--;
        return i;
    }

    static bool IsClosingMarkup(char c)
        => Char.IsWhiteSpace(c) || c is '"' or '\'' or '*' or ')' or ']' or '}' or '`' or '_' or '~' or '”' or '’';

    static bool IsTerminator(char c)
        => c is '?' or '!' or '.' or '？' or '！' or '。';
}


static class SessionLoader
{
    /// <summary>
    /// The prompt working set (non-compacted messages). A <see cref="MessageStatus.Streaming"/> message
    /// written by another process run belongs to a process that died mid-turn, so it is marked interrupted
    /// (lazy recovery, no start-up sweep). One from this run belongs to a live instance and is left alone.
    /// </summary>
    public static async Task<List<AiMessageDocument>> LoadWorkingSet(IAiSessionStore store, string sessionId, CancellationToken ct)
    {
        var messages = (await store
            .GetMessages(new MessageQuery(sessionId, IncludeCompacted: false), ct)
            .ConfigureAwait(false))
            .ToList();

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Status != MessageStatus.Streaming || msg.RunId == ProcessRun.Id)
                continue;

            var recovered = msg with { Status = MessageStatus.Interrupted };
            await store.UpdateMessage(recovered, ct).ConfigureAwait(false);
            messages[i] = recovered;
        }
        return messages;
    }
}
