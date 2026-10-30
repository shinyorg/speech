using System.Runtime.CompilerServices;

namespace Shiny.AiConversation.Infrastructure;

sealed class AiTurn : IAiTurn
{
    readonly Lock sync = new();
    readonly List<AiTurnEvent> events = [];
    readonly TaskCompletionSource<AiTurnResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource cts;
    readonly Action<AiTurn, string?> interruptedAfterCompletion;
    TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool ended;


    public AiTurn(string turnId, AiInput input, CancellationToken ct, Action<AiTurn, string?> interruptedAfterCompletion)
    {
        this.TurnId = turnId;
        this.Input = input;
        this.cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        this.interruptedAfterCompletion = interruptedAfterCompletion;
    }


    public string TurnId { get; }
    public AiInput Input { get; }
    public CancellationToken Token => this.cts.Token;
    public Task<AiTurnResult> Completion => this.completion.Task;
    public IAsyncEnumerable<AiTurnEvent> Events => this.ReadEvents();

    /// <summary>
    /// Why the turn was stopped; null when it wasn't (or was cancelled by the caller's token).
    /// </summary>
    public AiTurnOutcome? StopReason { get; private set; }
    public string? SpokenText { get; private set; }


    public void Interrupt(string? spokenText = null)
    {
        bool afterCompletion;
        lock (this.sync)
        {
            afterCompletion = this.ended;
            if (!this.ended && this.StopReason == null)
            {
                this.StopReason = AiTurnOutcome.Interrupted;
                this.SpokenText = spokenText;
            }
        }

        if (afterCompletion)
            this.interruptedAfterCompletion(this, spokenText);
        else
            this.Cancel();
    }


    internal void Stop(AiTurnOutcome reason)
    {
        lock (this.sync)
        {
            if (this.ended || this.StopReason != null)
                return;

            this.StopReason = reason;
        }
        this.Cancel();
    }


    internal void Add(AiTurnEvent e)
    {
        TaskCompletionSource signal;
        lock (this.sync)
        {
            this.events.Add(e);
            signal = this.changed;
            this.changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }


    internal void End(AiTurnResult result)
    {
        TaskCompletionSource signal;
        lock (this.sync)
        {
            this.ended = true;
            signal = this.changed;
        }
        signal.TrySetResult();
        this.completion.TrySetResult(result);
        this.cts.Dispose();
    }


    void Cancel()
    {
        try
        {
            this.cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the turn ended in between
        }
    }


    async IAsyncEnumerable<AiTurnEvent> ReadEvents([EnumeratorCancellation] CancellationToken ct = default)
    {
        var index = 0;
        while (true)
        {
            AiTurnEvent? next = null;
            Task wait;
            bool done;
            lock (this.sync)
            {
                if (index < this.events.Count)
                    next = this.events[index++];

                done = next == null && this.ended;
                wait = this.changed.Task;
            }

            if (done)
                yield break;

            if (next != null)
                yield return next;
            else
                await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
