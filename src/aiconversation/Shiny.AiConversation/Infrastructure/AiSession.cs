using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Shiny.AiConversation.Infrastructure;

sealed class AiSession : IAiSession
{
    const string VoiceModePrompt =
        "Your replies are spoken aloud. Use plain conversational prose: no markdown, lists, tables, code or emoji. " +
        "Keep replies short and ask at most one question at a time.";

    const string FlushPrompt =
        "Older messages in this conversation are about to be summarized and dropped from your context. " +
        "Save anything you will need later to memory now (facts, preferences, decisions, work in progress), then reply with OK.";

    readonly IAiSessionStore store;
    readonly IChatClientProvider chatClientProvider;
    readonly IReadOnlyList<ISessionInterceptor> interceptors;
    readonly TimeProvider time;
    readonly ILogger logger;
    readonly IContextCompactor compactor;
    readonly IAiAttachmentStore attachments;
    readonly SessionMemory? memory;
    readonly Dictionary<string, (byte[] Data, string MediaType)> ephemeralImages = new();
    readonly BroadcastChannel<AiSessionEvent> events = new(e => e is TurnEventOccurred { Event: TextDelta });
    readonly SemaphoreSlim headerGate = new(1, 1);
    readonly Lock sync = new();
    readonly List<(AITool Tool, AiToolFlags Flags)> sessionTools = [];
    readonly List<string> sessionPrompts = [];

    AiSessionDocument header;
    List<AiMessageDocument> workingSet;
    AiSessionState state;
    AiTurn? activeTurn;
    Task queue = Task.CompletedTask;
    bool disposed;


    internal AiSession(
        AiSessionDocument header,
        List<AiMessageDocument> workingSet,
        IAiSessionStore store,
        IChatClientProvider chatClientProvider,
        IReadOnlyList<ISessionInterceptor> interceptors,
        TimeProvider time,
        IContextCompactor compactor,
        IAiAttachmentStore attachments,
        IAiMemoryStore memoryStore,
        AiMemoryOptions memoryOptions,
        ILogger logger
    )
    {
        this.header = header;
        this.compactor = compactor;
        this.attachments = attachments;
        this.workingSet = workingSet;
        this.store = store;
        this.chatClientProvider = chatClientProvider;
        this.interceptors = interceptors;
        this.time = time;
        this.logger = logger;
        this.state = header.PendingQuestions.Count > 0 ? AiSessionState.AwaitingUser : AiSessionState.Idle;

        var profile = header.Settings.Profile;
        if (memoryOptions.Enabled && (profile == null || !memoryOptions.DisabledProfiles.Contains(profile)))
        {
            this.memory = new SessionMemory(
                memoryStore,
                memoryOptions,
                time,
                header.SessionId,
                profile,
                (doc, change) => this.Publish(new MemoryChanged(this.SessionId, this.time.GetUtcNow(), doc, change)),
                (scope, key, used, limit) => this.Publish(new MemoryPressureChanged(this.SessionId, this.time.GetUtcNow(), scope, key, used, limit))
            );
        }
    }


    public string SessionId => this.header.SessionId;
    public string? Title { get { lock (this.sync) return this.header.Title; } }
    public DateTimeOffset Started => this.header.Started;
    public AiSessionSettings Settings { get { lock (this.sync) return this.header.Settings; } }
    public AiSessionState State { get { lock (this.sync) return this.state; } }
    public IReadOnlyList<AiQuestion> PendingQuestions { get { lock (this.sync) return this.header.PendingQuestions; } }
    public SessionUsage Usage { get { lock (this.sync) return this.header.Usage; } }
    public ContextSnapshot Context { get { lock (this.sync) return this.header.Context; } }
    public IAiTurn? ActiveTurn { get { lock (this.sync) return this.activeTurn; } }

    /// <summary>
    /// The prompt working set (non-compacted messages), for tests and diagnostics.
    /// </summary>
    internal IReadOnlyList<AiMessageDocument> WorkingSet { get { lock (this.sync) return this.workingSet.ToList(); } }


    internal async Task Load(bool isNew, CancellationToken ct)
    {
        var ctx = new SessionLoadedContext(this, isNew);
        foreach (var interceptor in this.interceptors)
        {
            try
            {
                await interceptor.OnSessionLoaded(ctx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Interceptor {Interceptor} failed loading session {SessionId}", interceptor.GetType().Name, this.SessionId);
            }
        }

        lock (this.sync)
        {
            this.sessionTools.AddRange(ctx.Tools);
            this.sessionPrompts.AddRange(ctx.SystemPrompts);
            if (this.memory != null)
                this.sessionTools.Add((this.memory.Tool, AiToolFlags.Silent));
        }

        // a resumed session gets an estimate until its first real request
        this.SetContext(this.EstimateContext(), true, publish: false);
    }


    public IAsyncEnumerable<AiSessionEvent> GetEvents(CancellationToken ct = default)
        => this.events.Subscribe(() =>
        {
            lock (this.sync)
                return new Snapshot(this.SessionId, this.time.GetUtcNow(), this.state, this.header.PendingQuestions, this.header.Usage, this.header.Context);
        }, ct);


    public IAiTurn Send(AiInput input, CancellationToken ct = default)
    {
        AiTurn? previous;
        AiTurn turn;
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);

            previous = this.activeTurn;
            turn = new AiTurn(IdGenerator.New(this.time), input, ct, this.OnInterruptedAfterCompletion);
            this.activeTurn = turn;
            _ = this.Enqueue(() => this.RunTurn(turn));
        }
        previous?.Stop(AiTurnOutcome.Superseded);
        return turn;
    }


    public async Task<MessagePage> GetMessages(long? beforeSequence, int count, CancellationToken ct = default)
    {
        var list = await this.store
            .GetMessages(new MessageQuery(this.SessionId, BeforeSequence: beforeSequence, Limit: count + 1), ct)
            .ConfigureAwait(false);

        var hasMore = list.Count > count;
        return new MessagePage(hasMore ? list.Skip(1).ToList() : list, hasMore);
    }


    public Task Compact(CancellationToken ct = default)
    {
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            return this.Enqueue(() => this.CompactCore(force: true, ct));
        }
    }


    public async Task SetTitle(string title, CancellationToken ct = default)
    {
        await this.SaveHeader(h => h with { Title = title }, retryOnConflict: true, ct).ConfigureAwait(false);
        this.Publish(new TitleChanged(this.SessionId, this.time.GetUtcNow(), title));
    }


    public async Task UpdateSettings(Func<AiSessionSettings, AiSessionSettings> update, CancellationToken ct = default)
    {
        await this.SaveHeader(h => h with { Settings = update(h.Settings) }, retryOnConflict: true, ct).ConfigureAwait(false);

        // thresholds or the context window may have changed
        ContextSnapshot current;
        lock (this.sync)
            current = this.header.Context;

        this.SetContext(current.CurrentTokens, current.IsEstimate, publish: true);
    }


    public async ValueTask DisposeAsync()
    {
        AiTurn? turn;
        Task pending;
        lock (this.sync)
        {
            if (this.disposed)
                return;

            this.disposed = true;
            turn = this.activeTurn;
            pending = this.queue;
        }

        turn?.Interrupt();
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Error finishing session {SessionId} on dispose", this.SessionId);
        }
        this.events.Complete();
    }


    /// <summary>
    /// Turns and compaction run one at a time, in order. Call under <see cref="sync"/>.
    /// </summary>
    Task Enqueue(Func<Task> work)
    {
        var task = this.queue.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        this.queue = task.ContinueWith(_ => { }, TaskScheduler.Default);   // a failed step never breaks the chain
        return task;
    }


    async Task RunTurn(AiTurn turn)
    {
        var updates = new List<ChatResponseUpdate>();
        var usage = new UsageAccumulator();
        IReadOnlyList<AiQuestion>? asked = null;
        List<ChatMessage>? prompt = null;
        List<AITool>? tools = null;
        AiMessageDocument? reply = null;
        TurnContext? turnContext = null;
        var turnUserMessages = new HashSet<string>();
        AiTurnResult result;

        try
        {
            await this.RefreshIfChanged().ConfigureAwait(false);

            // safety net: never send a request that's over the auto-compaction threshold
            if (this.NeedsCompaction())
                await this.CompactCore(force: false, CancellationToken.None).ConfigureAwait(false);

            // L6: each turn replaces the pending questions
            lock (this.sync)
                this.header = this.header with { PendingQuestions = [] };

            // write early (P6): the user message is stored before anything can fail or be cancelled
            var userMessage = await this.AppendInput(turn.Input).ConfigureAwait(false);
            turnUserMessages.Add(userMessage.MessageId);

            this.AddTurnEvent(turn, new TurnStarted(turn.TurnId, this.time.GetUtcNow(), userMessage));
            turn.Token.ThrowIfCancellationRequested();

            turnContext = new TurnContext(this, turn.TurnId, turn.Input);
            foreach (var interceptor in this.interceptors)
            {
                try
                {
                    await interceptor.OnBeforeTurn(turnContext).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Interceptor {Interceptor} failed before turn", interceptor.GetType().Name);
                    this.Publish(new SessionError(this.SessionId, this.time.GetUtcNow(), ex));
                }
            }
            this.SetState(AiSessionState.Thinking);

            reply = await this.Append(this.NewMessage(MessageRole.Assistant, MessageSource.Model, MessageStatus.Streaming, [])).ConfigureAwait(false);

            this.memory?.BeginTurn();
            var memoryIndex = await this.TryBuildMemoryIndex(turn.Token).ConfigureAwait(false);

            var voice = turn.Input.Source == MessageSource.Voice;
            var toolSet = this.GetTools(turnContext, voice, q => asked = q);
            tools = toolSet.Select(x => x.Tool).ToList();

            var client = await this.chatClientProvider.GetChatClient(turn.Token).ConfigureAwait(false);
            if (client.GetService<FunctionInvokingChatClient>() == null)
                client = client.AsBuilder().UseFunctionInvocation().Build();

            var options = new ChatOptions { Tools = tools };
            var callNames = new Dictionary<string, string>();
            List<MessagePart> parts;
            while (true)
            {
                var images = await this.LoadPromptImages(turnUserMessages, turn.Token).ConfigureAwait(false);
                prompt = this.BuildPrompt(turnContext, turn.Input, memoryIndex, images);

                updates.Clear();
                await foreach (var update in client.GetStreamingResponseAsync(prompt, options, turn.Token).ConfigureAwait(false))
                {
                    updates.Add(update);
                    foreach (var content in update.Contents)
                        this.OnContent(turn, content, toolSet, callNames, usage);
                }
                parts = MessageMapper.ToParts(updates.ToChatResponse().Messages);

                // a tool (e.g. the camera) ended the round and queued more input: keep going in this same turn
                var followUps = asked == null ? turnContext.TakeFollowUps() : [];
                if (followUps.Count == 0)
                    break;

                reply = await this.Finalize(reply, MessageStatus.Complete, parts, null, usage).ConfigureAwait(false);
                foreach (var followUp in followUps)
                    turnUserMessages.Add((await this.AppendInput(followUp).ConfigureAwait(false)).MessageId);

                reply = await this.Append(this.NewMessage(MessageRole.Assistant, MessageSource.Model, MessageStatus.Streaming, [])).ConfigureAwait(false);
            }
            var text = MessageMapper.GetText(parts);

            // L8: a model that never calls ask_user but ends on a question still keeps the conversation open
            var questions = asked ?? (QuestionHeuristic.EndsWithQuestion(text)
                ? [new AiQuestion(QuestionHeuristic.QuestionId, QuestionHeuristic.LastQuestion(text!))]
                : []);

            reply = await this.Finalize(reply, MessageStatus.Complete, parts, null, usage).ConfigureAwait(false);
            if (questions.Count > 0)
                this.AddTurnEvent(turn, new QuestionsAsked(turn.TurnId, this.time.GetUtcNow(), questions));

            var context = this.MeasureContext(usage, prompt, reply, tools);
            await this.SaveTurnHeader(questions, usage, context, retryOnConflict: false).ConfigureAwait(false);

            var outcome = questions.Count > 0 ? AiTurnOutcome.AwaitingUser : AiTurnOutcome.Completed;
            result = new AiTurnResult(outcome, reply, questions, usage.ToSnapshot());

            this.SetState(outcome == AiTurnOutcome.AwaitingUser ? AiSessionState.AwaitingUser : AiSessionState.Idle);
            this.AddTurnEvent(turn, new TurnCompleted(turn.TurnId, this.time.GetUtcNow(), result));

            // compact in the background now, so the next (voice) turn doesn't wait for it
            if (this.NeedsCompaction())
            {
                lock (this.sync)
                {
                    if (!this.disposed)
                        _ = this.Enqueue(() => this.CompactCore(force: false, CancellationToken.None));
                }
            }
        }
        catch (OperationCanceledException) when (turn.Token.IsCancellationRequested)
        {
            // L9: interruption and supersession are normal, not errors
            var outcome = turn.StopReason ?? AiTurnOutcome.Interrupted;
            reply = await this.TryFinalizePartial(reply, MessageStatus.Interrupted, updates, turn.SpokenText, usage).ConfigureAwait(false);
            await this.TrySaveTurnHeader(usage, prompt, reply, tools).ConfigureAwait(false);

            result = new AiTurnResult(outcome, reply, [], usage.ToSnapshot());
            this.SetState(AiSessionState.Idle);
            this.AddTurnEvent(turn, new TurnInterrupted(turn.TurnId, this.time.GetUtcNow(), result, turn.SpokenText));
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Turn {TurnId} failed in session {SessionId}", turn.TurnId, this.SessionId);
            reply = await this.TryFinalizePartial(reply, MessageStatus.Failed, updates, null, usage).ConfigureAwait(false);
            if (ex is not SessionConflictException)
                await this.TrySaveTurnHeader(usage, prompt, reply, tools).ConfigureAwait(false);

            result = new AiTurnResult(AiTurnOutcome.Failed, reply, [], usage.ToSnapshot(), ex);
            this.Publish(new SessionError(this.SessionId, this.time.GetUtcNow(), ex));
            this.SetState(AiSessionState.Idle);
            this.AddTurnEvent(turn, new TurnFailed(turn.TurnId, this.time.GetUtcNow(), result, ex));
        }

        if (turnContext != null)
        {
            foreach (var interceptor in this.interceptors)
            {
                try
                {
                    await interceptor.OnAfterTurn(turnContext, result).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Interceptor {Interceptor} failed after turn", interceptor.GetType().Name);
                }
            }
        }

        lock (this.sync)
        {
            if (this.activeTurn == turn)
                this.activeTurn = null;

            // DontStore images exist only for the turn they arrived in
            this.ephemeralImages.Clear();
        }
        turn.End(result);
    }


    void OnContent(
        AiTurn turn,
        AIContent content,
        IReadOnlyList<(AITool Tool, AiToolFlags Flags)> toolSet,
        Dictionary<string, string> callNames,
        UsageAccumulator usage
    )
    {
        var now = this.time.GetUtcNow();
        switch (content)
        {
            case TextContent t when !String.IsNullOrEmpty(t.Text):
                this.SetState(AiSessionState.Responding);
                this.AddTurnEvent(turn, new TextDelta(turn.TurnId, now, t.Text));
                break;

            case FunctionCallContent f:
                var silent = toolSet.Any(x => x.Tool.Name == f.Name && x.Flags.HasFlag(AiToolFlags.Silent));
                if (!silent)
                    this.SetState(AiSessionState.Thinking);

                callNames[f.CallId] = f.Name;
                this.AddTurnEvent(turn, new ToolCallStarted(turn.TurnId, now, f.CallId, f.Name, MessageMapper.SerializeArguments(f.Arguments), silent));
                break;

            case FunctionResultContent r:
                var name = callNames.GetValueOrDefault(r.CallId) ?? String.Empty;
                this.AddTurnEvent(turn, new ToolCallCompleted(turn.TurnId, now, r.CallId, name, MessageMapper.ResultToString(r.Result), r.Exception != null));
                break;

            case UsageContent u:
                usage.Add(u.Details);
                break;
        }
    }


    List<(AITool Tool, AiToolFlags Flags)> GetTools(TurnContext turnContext, bool voice, Action<IReadOnlyList<AiQuestion>> onAsked)
    {
        var list = new List<(AITool, AiToolFlags)>();
        lock (this.sync)
            list.AddRange(this.sessionTools);

        list.AddRange(turnContext.Tools);
        list.Add((new AskUserFunction(voice, onAsked), AiToolFlags.Silent));
        return list;
    }


    /// <summary>
    /// Prompt assembly order: interceptor prompts, mode instructions, memory index, rolling summary, then
    /// non-compacted history (which ends with the new user message).
    /// </summary>
    List<ChatMessage> BuildPrompt(TurnContext? turnContext, AiInput? input, string? memoryIndex = null, IReadOnlyDictionary<string, DataContent>? images = null)
    {
        var system = new List<string>();
        List<AiMessageDocument> history;
        string? summary;
        lock (this.sync)
        {
            system.AddRange(this.sessionPrompts);
            history = this.workingSet.ToList();
            summary = this.header.Summary;
        }

        if (turnContext != null)
            system.AddRange(turnContext.SystemPrompts);

        if (input?.Source == MessageSource.Voice)
            system.Add(VoiceModePrompt);

        if (input != null)
            system.AddRange(input.Instructions);

        if (memoryIndex != null)
            system.Add(memoryIndex);

        var messages = new List<ChatMessage>();
        if (system.Count > 0)
            messages.Add(new ChatMessage(ChatRole.System, String.Join("\n\n", system)));

        if (!String.IsNullOrWhiteSpace(summary))
            messages.Add(new ChatMessage(ChatRole.System, $"{MessageMapper.SummaryPrefix}\n{summary}"));

        messages.AddRange(MessageMapper.ToChatMessages(history, images));
        return messages;
    }


    /// <summary>
    /// Stores the input's images (or keeps <see cref="ImageRetention.DontStore"/> ones in memory) and appends
    /// the user message referencing them.
    /// </summary>
    async Task<AiMessageDocument> AppendInput(AiInput input)
    {
        var parts = input.Parts.ToList();
        foreach (var image in input.Images)
        {
            AttachmentRef attachment;
            if (image.Retention == ImageRetention.DontStore)
            {
                attachment = new AttachmentRef("ephemeral:" + Guid.NewGuid().ToString("N"), image.MediaType, image.Data.Length);
                lock (this.sync)
                    this.ephemeralImages[attachment.AttachmentId] = (image.Data.ToArray(), image.MediaType);
            }
            else
            {
                attachment = await this.attachments.Save(image.Data, image.MediaType, this.SessionId, CancellationToken.None).ConfigureAwait(false);
            }
            parts.Add(new ImagePart(attachment, image.Description, image.Retention));
        }

        return await this.Append(this.NewMessage(MessageRole.User, input.Source, MessageStatus.Complete, parts)).ConfigureAwait(false);
    }


    /// <summary>
    /// The image bytes this prompt includes: images kept in context, plus those that arrived in this turn.
    /// Everything else is sent as a short "[image: …]" mention.
    /// </summary>
    async Task<Dictionary<string, DataContent>?> LoadPromptImages(HashSet<string> turnUserMessages, CancellationToken ct)
    {
        List<(ImagePart Part, string MessageId)> wanted;
        lock (this.sync)
        {
            wanted = this.workingSet
                .Where(m => m.Role == MessageRole.User)
                .SelectMany(m => m.Parts.OfType<ImagePart>().Select(p => (Part: p, m.MessageId)))
                .Where(x => x.Part.Retention == ImageRetention.KeepInContext || turnUserMessages.Contains(x.MessageId))
                .ToList();
        }
        if (wanted.Count == 0)
            return null;

        var images = new Dictionary<string, DataContent>();
        foreach (var (part, _) in wanted)
        {
            byte[]? data = null;
            lock (this.sync)
            {
                if (this.ephemeralImages.TryGetValue(part.Attachment.AttachmentId, out var e))
                    data = e.Data;
            }

            if (data == null && !part.Attachment.AttachmentId.StartsWith("ephemeral:", StringComparison.Ordinal))
            {
                try
                {
                    data = await this.attachments.Load(part.Attachment.AttachmentId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger.LogWarning(ex, "Failed to load attachment {AttachmentId}", part.Attachment.AttachmentId);
                }
            }

            if (data != null)
                images[part.Attachment.AttachmentId] = new DataContent(data, part.Attachment.MediaType);
        }
        return images;
    }


    async Task<string?> TryBuildMemoryIndex(CancellationToken ct)
    {
        if (this.memory == null)
            return null;

        try
        {
            return await this.memory.BuildIndex(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Failed to build the memory index for session {SessionId}", this.SessionId);
            return null;
        }
    }


    bool NeedsCompaction()
    {
        ContextSnapshot context;
        AiSessionSettings settings;
        int count;
        lock (this.sync)
        {
            context = this.header.Context;
            settings = this.header.Settings;
            count = this.workingSet.Count(x => x.Role != MessageRole.Summary);
        }

        if (settings.Compaction.AutoCompactThreshold is { } threshold && context.PercentUsed >= threshold)
            return true;

        return settings.Compaction.MaxMessages is { } max && count > max;
    }


    /// <summary>
    /// Rolling compaction: the previous summary plus the oldest messages become one new summary; the
    /// originals are marked compacted (kept for history and search). A failed summary degrades to a hard
    /// trim, never a failed turn. Runs on the turn queue, so it never overlaps a turn.
    /// </summary>
    async Task CompactCore(bool force, CancellationToken ct)
    {
        if (!force && !this.NeedsCompaction())
            return;

        List<AiMessageDocument> candidates;
        List<AiMessageDocument> oldSummaries;
        AiSessionSettings settings;
        string? previousSummary;
        bool pending;
        lock (this.sync)
        {
            candidates = this.workingSet.Where(x => x.Role != MessageRole.Summary && x.Status != MessageStatus.Streaming).ToList();
            oldSummaries = this.workingSet.Where(x => x.Role == MessageRole.Summary).ToList();
            settings = this.header.Settings;
            previousSummary = this.header.Summary;
            pending = this.header.PendingQuestions.Count > 0;
        }

        var keep = Math.Max(0, settings.Compaction.KeepRecentMessages);
        if (settings.Compaction.MaxMessages is { } max)
            keep = Math.Min(keep, Math.Max(0, max - 1));

        if (pending)
            keep = Math.Max(keep, 1);   // never compact the reply that asked the pending questions

        var toCompact = candidates.Take(Math.Max(0, candidates.Count - keep)).ToList();
        if (toCompact.Count == 0)
            return;

        var stateBefore = this.State;
        this.SetState(AiSessionState.Compacting);
        try
        {
            var usage = new UsageAccumulator();
            if (settings.Compaction.FlushToMemory)
                await this.FlushToMemory(usage, ct).ConfigureAwait(false);

            string? summary = null;
            try
            {
                summary = await this.compactor.Summarize(previousSummary, toCompact, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                this.logger.LogWarning(ex, "Summarizing session {SessionId} failed; hard-trimming instead", this.SessionId);
            }

            var hardTrim = String.IsNullOrWhiteSpace(summary);
            AiMessageDocument? summaryMessage = null;
            if (!hardTrim)
            {
                summaryMessage = await this.Append(this.NewMessage(
                    MessageRole.Summary,
                    MessageSource.Compaction,
                    MessageStatus.Complete,
                    [new TextPart(summary!, Audience.Displayed)]
                )).ConfigureAwait(false);
            }

            // summaries never pile up: the previous summary message is compacted along with the originals
            var compacted = new List<string>();
            foreach (var msg in toCompact.Concat(oldSummaries))
            {
                await this.store.UpdateMessage(msg with { Compacted = true, CompactedIntoMessageId = summaryMessage?.MessageId }, CancellationToken.None).ConfigureAwait(false);
                compacted.Add(msg.MessageId);
            }

            lock (this.sync)
                this.workingSet.RemoveAll(x => compacted.Contains(x.MessageId));

            await this.SaveHeader(h => h with
            {
                Summary = hardTrim ? h.Summary : summary,
                Usage = usage.Requests > 0 ? usage.AddTo(h.Usage) : h.Usage
            }, retryOnConflict: true, CancellationToken.None).ConfigureAwait(false);

            this.SetContext(this.EstimateContext(), true, publish: true);
            this.Publish(new ContextCompacted(this.SessionId, this.time.GetUtcNow(), summaryMessage, toCompact.Count, hardTrim));

            var ctx = new CompactionContext(this, summaryMessage, hardTrim);
            foreach (var interceptor in this.interceptors)
            {
                try
                {
                    await interceptor.OnCompacted(ctx).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Interceptor {Interceptor} failed after compaction", interceptor.GetType().Name);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogError(ex, "Compaction failed for session {SessionId}", this.SessionId);
            this.Publish(new SessionError(this.SessionId, this.time.GetUtcNow(), ex));
            if (force)
                throw;
        }
        finally
        {
            this.SetState(stateBefore == AiSessionState.Compacting ? AiSessionState.Idle : stateBefore);
        }
    }


    /// <summary>
    /// Memory is the durable layer and the summary the lossy one, so before compacting the model gets one
    /// silent request to save what it will need. Nothing from it is persisted except memory writes.
    /// </summary>
    async Task FlushToMemory(UsageAccumulator usage, CancellationToken ct)
    {
        if (this.memory == null)
            return;

        try
        {
            this.memory.BeginTurn();
            var prompt = this.BuildPrompt(null, null, await this.memory.BuildIndex(ct).ConfigureAwait(false));
            prompt.Add(new ChatMessage(ChatRole.User, FlushPrompt));

            var client = await this.chatClientProvider.GetChatClient(ct).ConfigureAwait(false);
            if (client.GetService<FunctionInvokingChatClient>() == null)
                client = client.AsBuilder().UseFunctionInvocation().Build();

            await foreach (var update in client.GetStreamingResponseAsync(prompt, new ChatOptions { Tools = [this.memory.Tool] }, ct).ConfigureAwait(false))
            {
                foreach (var u in update.Contents.OfType<UsageContent>())
                    usage.Add(u.Details);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            this.logger.LogWarning(ex, "Memory flush before compaction failed for session {SessionId}", this.SessionId);
        }
    }


    long EstimateContext()
    {
        List<AITool> tools;
        lock (this.sync)
            tools = this.sessionTools.Select(x => x.Tool).ToList();

        tools.Add(new AskUserFunction(false, _ => { }));
        return TokenEstimator.Estimate(this.BuildPrompt(null, null), tools);
    }


    async Task RefreshIfChanged()
    {
        var stored = await this.store.GetSession(this.SessionId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session '{this.SessionId}' no longer exists");

        long version;
        lock (this.sync)
            version = this.header.Version;

        if (stored.Version == version)
            return;

        // another instance changed the session: reload before building the prompt
        var messages = await SessionLoader.LoadWorkingSet(this.store, this.SessionId, CancellationToken.None).ConfigureAwait(false);
        lock (this.sync)
        {
            this.header = stored;
            this.workingSet = messages;
        }
    }


    AiMessageDocument NewMessage(MessageRole role, MessageSource source, MessageStatus status, IReadOnlyList<MessagePart> parts) => new(
        IdGenerator.New(this.time),
        this.SessionId,
        0,
        AiMessageDocument.CurrentSchemaVersion,
        this.time.GetUtcNow(),
        role,
        source,
        status,
        parts,
        MessageMapper.GetText(parts),
        null,
        null,
        false,
        null,
        ProcessRun.Id
    );


    async Task<AiMessageDocument> Append(AiMessageDocument message)
    {
        var stored = await this.store.AppendMessage(message, CancellationToken.None).ConfigureAwait(false);
        lock (this.sync)
        {
            this.workingSet.Add(stored);
            this.header = this.header with
            {
                MessageCount = this.header.MessageCount + 1,
                LastSequence = Math.Max(this.header.LastSequence, stored.Sequence)
            };
        }
        this.Publish(new MessageAdded(this.SessionId, this.time.GetUtcNow(), stored));
        return stored;
    }


    async Task<AiMessageDocument> Finalize(
        AiMessageDocument message,
        MessageStatus status,
        IReadOnlyList<MessagePart> parts,
        string? spokenText,
        UsageAccumulator usage
    )
    {
        var updated = message with
        {
            Status = status,
            Parts = parts,
            Text = MessageMapper.GetText(parts),
            SpokenText = spokenText,
            Usage = usage.ToSnapshot()
        };
        await this.Replace(updated).ConfigureAwait(false);
        return updated;
    }


    async Task<AiMessageDocument?> TryFinalizePartial(
        AiMessageDocument? message,
        MessageStatus status,
        List<ChatResponseUpdate> updates,
        string? spokenText,
        UsageAccumulator usage
    )
    {
        if (message == null)
            return null;

        try
        {
            var parts = MessageMapper.ToParts(updates.ToChatResponse().Messages);
            return await this.Finalize(message, status, parts, spokenText, usage).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to finalize message {MessageId}", message.MessageId);
            return message;
        }
    }


    async Task Replace(AiMessageDocument message)
    {
        await this.store.UpdateMessage(message, CancellationToken.None).ConfigureAwait(false);
        lock (this.sync)
        {
            var index = this.workingSet.FindIndex(x => x.MessageId == message.MessageId);
            if (index >= 0)
                this.workingSet[index] = message;
        }
        this.Publish(new MessageUpdated(this.SessionId, this.time.GetUtcNow(), message));
    }


    void OnInterruptedAfterCompletion(AiTurn turn, string? spokenText)
    {
        // the reply finished streaming but was still being spoken: record what was actually heard (P4)
        lock (this.sync)
        {
            if (this.disposed)
                return;

            this.Enqueue(async () =>
            {
                try
                {
                    var result = await turn.Completion.ConfigureAwait(false);
                    if (result.Reply == null || result.Outcome is AiTurnOutcome.Failed or AiTurnOutcome.Interrupted or AiTurnOutcome.Superseded)
                        return;

                    AiMessageDocument? current;
                    lock (this.sync)
                        current = this.workingSet.FirstOrDefault(x => x.MessageId == result.Reply.MessageId);

                    if (current != null)
                        await this.Replace(current with { Status = MessageStatus.Interrupted, SpokenText = spokenText }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Failed to record interruption of turn {TurnId}", turn.TurnId);
                }
            });
        }
    }


    ContextSnapshot MeasureContext(UsageAccumulator usage, List<ChatMessage>? prompt, AiMessageDocument? reply, List<AITool>? tools)
    {
        if (usage.LastRequestTokens is { } measured)
            return this.CreateContext(measured, false);

        var messages = (prompt ?? []).ToList();
        if (reply != null)
            messages.AddRange(MessageMapper.ToChatMessages([reply with { Status = MessageStatus.Complete }]));

        return this.CreateContext(TokenEstimator.Estimate(messages, tools ?? []), true);
    }


    async Task SaveTurnHeader(IReadOnlyList<AiQuestion> questions, UsageAccumulator usage, ContextSnapshot context, bool retryOnConflict)
    {
        await this.SaveHeader(h => h with
        {
            PendingQuestions = questions,
            Usage = usage.AddTo(h.Usage)
        }, retryOnConflict, CancellationToken.None).ConfigureAwait(false);

        this.SetContext(context.CurrentTokens, context.IsEstimate, publish: true);
    }


    async Task TrySaveTurnHeader(UsageAccumulator usage, List<ChatMessage>? prompt, AiMessageDocument? reply, List<AITool>? tools)
    {
        try
        {
            await this.SaveTurnHeader([], usage, this.MeasureContext(usage, prompt, reply, tools), retryOnConflict: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to save session {SessionId}", this.SessionId);
        }
    }


    async Task SaveHeader(Func<AiSessionDocument, AiSessionDocument> change, bool retryOnConflict, CancellationToken ct)
    {
        await this.headerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                AiSessionDocument current;
                lock (this.sync)
                    current = this.header;

                var next = change(current) with
                {
                    Version = current.Version + 1,
                    LastActivity = this.time.GetUtcNow()
                };

                try
                {
                    await this.store.SaveSession(next, current.Version, ct).ConfigureAwait(false);
                    lock (this.sync)
                        this.header = next;

                    return;
                }
                catch (SessionConflictException) when (retryOnConflict && attempt == 0)
                {
                    var stored = await this.store.GetSession(this.SessionId, ct).ConfigureAwait(false);
                    if (stored == null)
                        throw;

                    // keep this instance's counters; take everything else from the newer header
                    lock (this.sync)
                    {
                        this.header = stored with
                        {
                            MessageCount = Math.Max(stored.MessageCount, this.header.MessageCount),
                            LastSequence = Math.Max(stored.LastSequence, this.header.LastSequence)
                        };
                    }
                }
            }
        }
        finally
        {
            this.headerGate.Release();
        }
    }


    ContextSnapshot CreateContext(long tokens, bool isEstimate)
    {
        AiSessionSettings settings;
        lock (this.sync)
            settings = this.header.Settings;

        long? max = settings.ContextWindow ?? this.chatClientProvider.ContextWindow;
        double? percent = max > 0 ? tokens / (double)max.Value : null;
        ContextPressure? pressure = percent switch
        {
            null => null,
            var p when p >= settings.ContextCriticalThreshold => ContextPressure.Critical,
            var p when p >= settings.ContextWarningThreshold => ContextPressure.Warning,
            _ => ContextPressure.Normal
        };
        return new ContextSnapshot(tokens, max, percent, pressure, isEstimate, this.time.GetUtcNow());
    }


    void SetContext(long tokens, bool isEstimate, bool publish)
    {
        var context = this.CreateContext(tokens, isEstimate);
        ContextPressure? previous;
        SessionUsage usage;
        lock (this.sync)
        {
            previous = this.header.Context.Pressure;
            this.header = this.header with { Context = context };
            usage = this.header.Usage;
        }

        if (!publish)
            return;

        var now = this.time.GetUtcNow();
        this.Publish(new UsageChanged(this.SessionId, now, usage, context));
        if (previous != context.Pressure)
            this.Publish(new ContextPressureChanged(this.SessionId, now, context));
    }


    void SetState(AiSessionState newState)
    {
        lock (this.sync)
        {
            if (this.state == newState)
                return;

            this.state = newState;
        }
        this.Publish(new StateChanged(this.SessionId, this.time.GetUtcNow(), newState));
    }


    void AddTurnEvent(AiTurn turn, AiTurnEvent e)
    {
        turn.Add(e);
        this.Publish(new TurnEventOccurred(this.SessionId, e.Timestamp, e));
    }


    void Publish(AiSessionEvent e) => this.events.Publish(e);
}
