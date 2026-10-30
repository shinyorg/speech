using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Shiny.AiConversation.Infrastructure;

namespace Shiny.AiConversation.Tests;

/// <summary>
/// Scripted streaming chat client. Each queued response is one model request; the function-invoking
/// client calls it again after tool calls, so a tool loop needs one response per round trip.
/// </summary>
public class FakeChatClient : IChatClient
{
    readonly Queue<Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> script = new();
    int responseCount;

    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    public FakeChatClient Reply(params AIContent[] contents)
    {
        this.script.Enqueue(ct => Stream(contents, null, ct));
        return this;
    }

    /// <summary>
    /// Streams <paramref name="before"/>, then waits for <paramref name="gate"/> (or cancellation), then streams <paramref name="after"/>.
    /// </summary>
    public FakeChatClient ReplyGated(TaskCompletionSource gate, AIContent[] before, params AIContent[] after)
    {
        this.script.Enqueue(ct => Stream(before, (gate, after), ct));
        return this;
    }

    public FakeChatClient Throw(Exception ex)
    {
        this.script.Enqueue(_ => Throwing(ex));
        return this;
    }

    async IAsyncEnumerable<ChatResponseUpdate> Stream(
        AIContent[] contents,
        (TaskCompletionSource Gate, AIContent[] After)? gated,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        var id = $"m{Interlocked.Increment(ref this.responseCount)}";
        await Task.Yield();
        foreach (var c in contents)
            yield return new ChatResponseUpdate(ChatRole.Assistant, [c]) { MessageId = id };

        if (gated != null)
        {
            await gated.Value.Gate.Task.WaitAsync(ct);
            foreach (var c in gated.Value.After)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [c]) { MessageId = id };
        }
    }

    static async IAsyncEnumerable<ChatResponseUpdate> Throwing(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        this.Requests.Add((messages.ToList(), options));
        if (this.script.Count == 0)
            throw new InvalidOperationException("No scripted response left");

        return this.script.Dequeue()(cancellationToken);
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => this.GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    public static TextContent Text(string text) => new(text);
    public static UsageContent Usage(long input, long output) => new(new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output });
    public static FunctionCallContent Call(string callId, string name, Dictionary<string, object?>? args = null) => new(callId, name, args);

    public static FunctionCallContent AskUser(string callId, string json)
        => new(callId, AskUserFunction.ToolName, new Dictionary<string, object?> { ["questions"] = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone() });
}


public class FakeCompactor : IContextCompactor
{
    public List<(string? Previous, IReadOnlyList<AiMessageDocument> Messages)> Calls { get; } = [];
    public Exception? Throw { get; set; }

    public Task<string> Summarize(string? previousSummary, IReadOnlyList<AiMessageDocument> messages, CancellationToken ct)
    {
        this.Calls.Add((previousSummary, messages));
        if (this.Throw != null)
            throw this.Throw;

        return Task.FromResult($"summary {this.Calls.Count}: " + String.Join(" / ", messages.Select(x => x.Text)));
    }
}


public class FakeChatClientProvider(IChatClient client, int? contextWindow = null) : IChatClientProvider
{
    public Task<IChatClient> GetChatClient(CancellationToken cancelToken = default) => Task.FromResult(client);
    public int? ContextWindow => contextWindow;
}


public class RecordingInterceptor : ISessionInterceptor
{
    public List<(string SessionId, bool IsNew)> Loaded { get; } = [];
    public List<AiTurnResult> After { get; } = [];
    public Action<SessionLoadedContext>? OnLoaded { get; set; }
    public Action<TurnContext>? OnBefore { get; set; }

    public Task OnSessionLoaded(SessionLoadedContext context)
    {
        this.Loaded.Add((context.Session.SessionId, context.IsNew));
        this.OnLoaded?.Invoke(context);
        return Task.CompletedTask;
    }

    public Task OnBeforeTurn(TurnContext context)
    {
        this.OnBefore?.Invoke(context);
        return Task.CompletedTask;
    }

    public Task OnAfterTurn(TurnContext context, AiTurnResult result)
    {
        this.After.Add(result);
        return Task.CompletedTask;
    }
}


/// <summary>
/// Collects session events in the background; the subscription is live (snapshot received) once Start returns.
/// </summary>
public sealed class EventCollector : IAsyncDisposable
{
    readonly CancellationTokenSource cts = new();
    readonly Lock sync = new();
    readonly List<AiSessionEvent> events = [];
    Task loop = Task.CompletedTask;

    public IReadOnlyList<AiSessionEvent> Events
    {
        get { lock (this.sync) return this.events.ToList(); }
    }

    public static async Task<EventCollector> Start(IAiSession session)
    {
        var c = new EventCollector();
        var e = session.GetEvents(c.cts.Token).GetAsyncEnumerator();
        await e.MoveNextAsync();
        c.Add(e.Current);
        c.loop = Task.Run(async () =>
        {
            try
            {
                while (await e.MoveNextAsync())
                    c.Add(e.Current);
            }
            finally
            {
                await e.DisposeAsync();
            }
        });
        return c;
    }

    void Add(AiSessionEvent e)
    {
        lock (this.sync)
            this.events.Add(e);
    }

    public async Task WaitFor(Func<IReadOnlyList<AiSessionEvent>, bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition(this.Events))
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Condition not met");

            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.cts.Cancel();
        await this.loop;
    }
}


public static class TestHelpers
{
    public static (AiSessionManager Manager, InMemoryAiSessionStore Store, FakeChatClient Chat) Create(
        int? contextWindow = null,
        params ISessionInterceptor[] interceptors
    )
    {
        var chat = new FakeChatClient();
        var store = new InMemoryAiSessionStore();
        // memory off by default so tests see only the tools they add; memory tests turn it on
        var manager = new AiSessionManager(store, new FakeChatClientProvider(chat, contextWindow), interceptors, TimeProvider.System, memoryOptions: new AiMemoryOptions { Enabled = false });
        return (manager, store, chat);
    }

    public static Harness CreateHarness(
        int? contextWindow = null,
        AiMemoryOptions? memory = null,
        IAiSessionStore? store = null,
        IAiMemoryStore? memoryStore = null,
        params ISessionInterceptor[] interceptors
    )
    {
        var h = new Harness(
            new FakeChatClient(),
            store ?? new InMemoryAiSessionStore(),
            memoryStore ?? new InMemoryAiMemoryStore(),
            new FakeCompactor()
        );
        h.Manager = new AiSessionManager(
            h.Store,
            new FakeChatClientProvider(h.Chat, contextWindow),
            interceptors,
            TimeProvider.System,
            h.MemoryStore,
            memory ?? new AiMemoryOptions { Enabled = false },
            h.Compactor
        );
        return h;
    }

    public static async Task<AiTurnResult> WaitAsync(this IAiTurn turn)
        => await turn.Completion.WaitAsync(TimeSpan.FromSeconds(5));

    public static IEnumerable<string> Texts(this IEnumerable<ChatMessage> messages, ChatRole role)
        => messages.Where(x => x.Role == role).Select(x => x.Text);
}


public record Harness(FakeChatClient Chat, IAiSessionStore Store, IAiMemoryStore MemoryStore, FakeCompactor Compactor)
{
    public AiSessionManager Manager { get; set; } = null!;
}
