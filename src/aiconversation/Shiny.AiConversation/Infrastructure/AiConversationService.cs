using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shiny.Audio;
using Shiny.Speech;

namespace Shiny.AiConversation.Infrastructure;

enum InterruptionKind { None, QuietWord, NewUtterance }
record InterruptionResult(InterruptionKind Kind, string? Text = null, float? Confidence = null);

public class AiConversationService(
    IChatClientProvider chatClientProvider,
    ISpeechToTextService speechToText,
    ITextToSpeechService textToSpeech,
    ILogger<AiConversationService>? logger,
    ISoundProvider soundProvider,
    IEnumerable<IContextProvider> contextProviders,
    IMessageStore? messageStore = null
) : IAiConversationService, IAsyncDisposable
{
    readonly SemaphoreSlim semaphore = new(1, 1);
    readonly object sessionLock = new();
    SessionMode sessionMode = SessionMode.None;
    CancellationTokenSource? sessionCts;
    Channel<SpeechRecognitionResult>? recognitionChannel;
    Channel<string>? keywordChannel;
    bool sessionStarted;
    bool lastResponseExpectedReply;
    long suppressResultsUntilTicks;
    AiQuestion[] pendingQuestions = [];

    static readonly TimeSpan TtsEchoSettleWindow = TimeSpan.FromMilliseconds(750);

    // Used only by AiStructuredOutputMode.Prompt, where nothing on the request constrains the format.
    const string AiTurnSchemaPrompt =
        """
        Respond with only a JSON object in exactly this shape, with no surrounding text or markdown:
        {"reply":"string","questions":[{"id":"string","text":"string","choices":[{"id":"string","label":"string"}],"allowMultiple":false}]}
        'questions' and 'choices' may be omitted or empty.
        """;

    public event Action<AiState>? StatusChanged;
    public event Action<AiResponse>? AiResponded;
    public event Action<SpeechRecognitionResult>? SpeechResultReceived;
    public event Action<ConversationSpeech>? SpeechOccurred;
    public event Action<Exception>? ErrorOccurred;
    public bool IsWakeWordEnabled => this.sessionMode == SessionMode.WakeWord;
    public string? WakeWord { get; private set; }
    public bool InterruptionEnabled { get; set; }
    public IList<string>? QuietWords { get; set; } = ["cancel", "quiet", "shut up", "stop", "nevermind", "never mind", "hush"];
    public SpeechRecognitionOptions? SpeechToTextOptions { get; set; }
    public Shiny.Speech.TextToSpeechOptions? TextToSpeechOptions { get; set; }
    public AiState Status { get; private set; }
    public AiAcknowledgement Acknowledgement { get; set; } = AiAcknowledgement.Full;
    public float InterruptionMinConfidence { get; set; } = 0.5f;
    public TimeSpan? FollowUpTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public AiStructuredOutputMode? StructuredOutputMode { get; set; }
    public IReadOnlyList<AiQuestion> PendingQuestions => this.pendingQuestions;

    List<ChatMessage> currentMessages = [];
    public IReadOnlyList<ChatMessage> CurrentChatMessages => currentMessages.AsReadOnly();

    public void ClearCurrentChat()
    {
        this.currentMessages.Clear();
        this.ClearPendingQuestions();
    }

    public async Task StartWakeWord(string wakeWord)
    {
        if (this.sessionMode != SessionMode.None)
            throw new InvalidOperationException("A conversation session is already active.");

        logger?.LogDebug("Starting wake word detection with word: {WakeWord}", wakeWord);
        this.WakeWord = wakeWord;
        await this.StartSessionAsync(SessionMode.WakeWord, [wakeWord]).ConfigureAwait(false);
        var ct = this.sessionCts!.Token;

        _ = Task.Run(() => this.RunWakeWordLoop(wakeWord, ct), ct);
    }

    public Task StopWakeWord()
    {
        logger?.LogDebug("Stopping wake word detection");
        if (this.sessionMode != SessionMode.WakeWord)
            return Task.CompletedTask;

        return this.EndSessionAsync();
    }

    public async Task ListenAndTalk(CancellationToken cancellationToken)
    {
        if (this.sessionMode == SessionMode.WakeWord)
            throw new InvalidOperationException("Cannot use ListenAndTalk while wake word is active.");

        if (this.sessionMode != SessionMode.None)
            throw new InvalidOperationException("A conversation session is already active.");

        logger?.LogDebug("ListenAndTalk started");
        await this.StartSessionAsync(SessionMode.PushToTalk, null).ConfigureAwait(false);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.sessionCts!.Token);
        var ct = linkedCts.Token;

        try
        {
            var continueListening = true;
            var isFollowUp = false;
            while (continueListening && !ct.IsCancellationRequested)
            {
                this.lastResponseExpectedReply = false;
                this.SetStatus(AiState.Listening);
                await this.SafePlay(AiAction.Ok).ConfigureAwait(false);

                // The first turn is user-initiated (they tapped the mic) so it waits as long as it takes.
                // Follow-ups are the AI's idea, so they time out rather than hold the mic open forever.
                var timeout = isFollowUp ? this.FollowUpTimeout : null;
                logger?.LogDebug("Listening for utterance (follow-up: {IsFollowUp}, timeout: {Timeout})", isFollowUp, timeout);
                var utterance = await this.ReadNextUtteranceAsync(ct, timeout).ConfigureAwait(false);
                logger?.LogDebug("Utterance received: {Utterance}", utterance);

                if (String.IsNullOrWhiteSpace(utterance))
                {
                    if (isFollowUp)
                        logger?.LogDebug("Follow-up window expired with no answer");

                    this.ClearPendingQuestions();
                    break;
                }

                await this.TalkTo(utterance, ct).ConfigureAwait(false);

                continueListening = this.lastResponseExpectedReply;
                isFollowUp = true;
                logger?.LogDebug("Continue listening: {ContinueListening}, ExpectsReply: {ExpectsReply}", continueListening, this.lastResponseExpectedReply);
            }

            this.SetStatus(AiState.Idle);
        }
        catch (OperationCanceledException)
        {
            logger?.LogDebug("ListenAndTalk cancelled");
            await this.SafePlay(AiAction.Cancel).ConfigureAwait(false);
            this.SetStatus(AiState.Idle);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "ListenAndTalk error");
            await this.SafePlay(AiAction.Error).ConfigureAwait(false);
            this.SetStatus(AiState.Idle);
            this.RaiseError(ex);
        }
        finally
        {
            await this.EndSessionAsync().ConfigureAwait(false);
        }
    }

    public async Task TalkTo(string message, CancellationToken cancellationToken)
    {
        logger?.LogDebug("TalkTo called with message: {Message}", message);
        await this.semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.ProcessMessage(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.SetStatus(AiState.Idle);
            this.semaphore.Release();
        }
    }

    async Task RunWakeWordLoop(string wakeWord, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                this.SetStatus(AiState.Listening);

                string? utterance;
                if (this.lastResponseExpectedReply)
                {
                    logger?.LogDebug(
                        "AI expects a reply ({QuestionCount} pending), capturing follow-up utterance directly",
                        this.pendingQuestions.Length
                    );
                    this.lastResponseExpectedReply = false;
                    utterance = await this.ReadNextUtteranceAsync(ct, this.FollowUpTimeout).ConfigureAwait(false);

                    if (String.IsNullOrWhiteSpace(utterance))
                    {
                        // Nobody answered - drop the questions and go back to requiring the wake word so
                        // the next unrelated thing said in the room isn't taken as the answer.
                        logger?.LogDebug("Follow-up window expired with no answer, returning to wake word");
                        this.ClearPendingQuestions();
                        continue;
                    }
                }
                else
                {
                    logger?.LogDebug("Waiting for wake word: {WakeWord}", wakeWord);
                    await this.WaitForKeywordAsync(ct).ConfigureAwait(false);
                    logger?.LogDebug("Wake word detected, capturing utterance");
                    utterance = await this.ReadNextUtteranceAsync(ct).ConfigureAwait(false);
                }

                logger?.LogDebug("Utterance received: {Utterance}", utterance);
                if (!String.IsNullOrWhiteSpace(utterance))
                    await this.TalkTo(utterance!, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            logger?.LogDebug("Wake word loop cancelled");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Wake word loop error");
            await this.SafePlay(AiAction.Error).ConfigureAwait(false);
            this.RaiseError(ex);
        }
        finally
        {
            this.SetStatus(AiState.Idle);
        }
    }

    async Task ProcessMessage(string message, CancellationToken cancellationToken)
    {
        this.SetStatus(AiState.Thinking);
        await this.SafePlay(AiAction.Think).ConfigureAwait(false);

        logger?.LogDebug("Getting chat client");
        var chatClient = await chatClientProvider
            .GetChatClient(cancellationToken)
            .ConfigureAwait(false);

        var chatMessages = this.CurrentChatMessages.ToList();
        var userMessage = new ChatMessage(ChatRole.User, message);
        chatMessages.Add(userMessage);

        var aiContext = new AiContext { Acknowledgement = this.Acknowledgement };
        foreach (var cp in contextProviders)
            await cp.Apply(aiContext).ConfigureAwait(false);

        chatMessages.AddRange(aiContext.SystemPrompts.Select(x => new ChatMessage(ChatRole.System, x)));

        var options = new ChatOptions
        {
            Tools = aiContext.Tools
        };

        var mode = this.StructuredOutputMode ?? chatClientProvider.StructuredOutputMode;
        if (mode != AiStructuredOutputMode.None)
            chatMessages.Add(new ChatMessage(ChatRole.System, this.BuildTurnContractPrompt()));

        logger?.LogDebug(
            "Sending {MessageCount} messages to chat client with {ToolCount} tools, Acknowledgement: {Acknowledgement}, StructuredOutput: {Mode}",
            chatMessages.Count,
            options.Tools.Count,
            this.Acknowledgement,
            mode
        );

        this.SetStatus(AiState.Responding);
        await this.SafePlay(AiAction.Respond).ConfigureAwait(false);

        var wasReadAloud = this.Acknowledgement > AiAcknowledgement.AudioBlip;
        var (response, turn) = await this
            .RequestTurn(chatClient, chatMessages, options, mode, cancellationToken)
            .ConfigureAwait(false);

        // With structured output response.Text is the JSON envelope; the reply is what the user sees
        // and hears. When parsing failed (or structured output is off) they are one and the same.
        var replyText = turn?.Reply ?? response.Text;

        logger?.LogDebug(
            "AI response received - Structured: {Structured}, ReplyLength: {ReplyLength}, Questions: {QuestionCount}, WasReadAloud: {WasReadAloud}",
            turn != null,
            replyText?.Length ?? 0,
            turn?.Questions?.Length ?? 0,
            wasReadAloud
        );

        this.currentMessages.Add(userMessage);

        // The raw text goes into history on purpose - keeping the JSON envelope in the transcript
        // anchors the model to the format on subsequent turns.
        if (response.Text is { } responseText)
            this.currentMessages.Add(new ChatMessage(ChatRole.Assistant, responseText));

        // Each turn replaces the queue: the model re-asks whatever it still needs, so anything it
        // stopped asking about is answered.
        this.pendingQuestions = turn?.Questions ?? [];

        var expectsResponse = turn?.ExpectsResponse ?? TextEndsWithQuestion(replyText);
        this.lastResponseExpectedReply = expectsResponse;
        logger?.LogDebug("ExpectsResponse: {ExpectsResponse} (structured: {Structured})", expectsResponse, turn != null);
        this.RaiseAiResponded(new AiResponse(response, wasReadAloud, expectsResponse, turn));

        if (messageStore != null)
        {
            await messageStore
                .Store(userMessage.Text, replyText, response, cancellationToken)
                .ConfigureAwait(false);
        }

        if (wasReadAloud && replyText is { } spokenText)
        {
            logger?.LogDebug("Starting TTS for response ({Length} chars), InterruptionEnabled: {InterruptionEnabled}", spokenText.Length, this.InterruptionEnabled);
            var interruption = await this.SpeakWithInterruptionSupport(spokenText, cancellationToken).ConfigureAwait(false);

            switch (interruption.Kind)
            {
                case InterruptionKind.QuietWord:
                    logger?.LogDebug("TTS interrupted by quiet word: {Word}", interruption.Text);
                    this.ClearPendingQuestions();
                    return;

                case InterruptionKind.NewUtterance:
                    logger?.LogDebug("TTS interrupted by new utterance: {Utterance}", interruption.Text);
                    await this.ProcessMessage(interruption.Text!, cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
        else
        {
            logger?.LogDebug("Skipping TTS - WasReadAloud: {WasReadAloud}, HasText: {HasText}", wasReadAloud, response.Text != null);
        }

        await this.SafePlay(AiAction.Ok).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues the chat request in whichever structured mode the provider supports and parses the turn
    /// out of it. Every path degrades to plain text rather than failing: an unparseable reply becomes a
    /// null turn, and the caller falls back to the wording heuristic for the "expects a reply" signal.
    /// </summary>
    async Task<(ChatResponse Response, AiTurn? Turn)> RequestTurn(
        IChatClient chatClient,
        List<ChatMessage> chatMessages,
        ChatOptions options,
        AiStructuredOutputMode mode,
        CancellationToken cancellationToken
    )
    {
        if (mode == AiStructuredOutputMode.None)
        {
            var plain = await chatClient.GetResponseAsync(chatMessages, options, cancellationToken).ConfigureAwait(false);
            return (plain, null);
        }

        if (mode == AiStructuredOutputMode.Prompt)
        {
            // No response-format constraint at all - the shape is described in the prompt and we parse
            // leniently, since models in this mode routinely wrap the JSON in markdown fences.
            chatMessages.Add(new ChatMessage(ChatRole.System, AiTurnSchemaPrompt));
            var prompted = await chatClient.GetResponseAsync(chatMessages, options, cancellationToken).ConfigureAwait(false);
            return (prompted, this.ParseTurn(prompted.Text));
        }

        try
        {
            var typed = await chatClient
                .GetResponseAsync<AiTurn>(
                    chatMessages,
                    AiTurnSerializer.JsonOptions,
                    options,
                    useJsonSchemaResponseFormat: mode == AiStructuredOutputMode.JsonSchema,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (typed.TryGetResult(out var result) && !String.IsNullOrWhiteSpace(result?.Reply))
                return (typed, result);

            // Schema-constrained requests can still come back with fenced or padded JSON on providers
            // that only approximate the format.
            return (typed, this.ParseTurn(typed.Text));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The endpoint rejected the response format outright (wrong model, proxy that strips it,
            // etc). Retrying unstructured is strictly better than surfacing an error to the user.
            logger?.LogWarning(
                ex,
                "Structured output request failed in {Mode} mode - retrying without it. Set IChatClientProvider.StructuredOutputMode to avoid the extra round trip.",
                mode
            );

            var fallback = await chatClient.GetResponseAsync(chatMessages, options, cancellationToken).ConfigureAwait(false);
            return (fallback, this.ParseTurn(fallback.Text));
        }
    }

    AiTurn? ParseTurn(string? text)
    {
        var turn = AiTurnSerializer.Parse(text);
        if (turn == null && !String.IsNullOrWhiteSpace(text))
            logger?.LogDebug("Response was not a parseable AiTurn, falling back to plain text handling");

        return turn;
    }

    string BuildTurnContractPrompt()
    {
        // Voice and text want different question density: a list of three questions reads fine as chips
        // and is unusable when spoken.
        var questionLimit = this.Acknowledgement > AiAcknowledgement.AudioBlip
            ? "Your reply is being spoken aloud, so ask at most one question per turn."
            : "You may ask more than one question in a turn when they are genuinely independent.";

        return
            "Reply with a single JSON object. Put the natural-language answer - everything the user should " +
            "read or hear - in 'reply', as plain prose with no JSON inside it. " +
            "When you need something back from the user before you can continue, add an entry to 'questions' " +
            "with a stable 'id' and the question text; leave 'questions' empty for a turn that needs nothing back. " +
            "When a question has a small fixed set of valid answers, list them in 'choices' with a stable 'id' " +
            "and a short 'label', and set 'allowMultiple' when more than one may be picked. " +
            "Still phrase the question naturally inside 'reply' - the structured questions drive the interface, " +
            "they are not shown to the user verbatim. " +
            questionLimit;
    }

    void ClearPendingQuestions()
    {
        this.pendingQuestions = [];
        this.lastResponseExpectedReply = false;
    }

    async Task<InterruptionResult> SpeakWithInterruptionSupport(string text, CancellationToken cancellationToken)
    {
        var quietWords = this.QuietWords;
        if (!this.InterruptionEnabled || quietWords == null || quietWords.Count == 0 || !this.sessionStarted)
        {
            logger?.LogDebug(
                "Speaking without interruption support (InterruptionEnabled: {Enabled}, QuietWords: {Count}, SessionActive: {Active})",
                this.InterruptionEnabled,
                quietWords?.Count ?? 0,
                this.sessionStarted
            );
            // Suppress recognition while TTS is speaking, plus a short settle window after, so the
            // recognizer's own transcription of the AI's voice doesn't become the next "utterance".
            Interlocked.Exchange(ref this.suppressResultsUntilTicks, DateTimeOffset.MaxValue.UtcTicks);
            this.RaiseSpeech(ConversationSpeechSource.Spoken, text);
            try
            {
                await textToSpeech.SpeakAsync(text, this.TextToSpeechOptions, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                var until = DateTimeOffset.UtcNow.Add(TtsEchoSettleWindow).UtcTicks;
                Interlocked.Exchange(ref this.suppressResultsUntilTicks, until);
                this.DrainRecognitionChannel();
            }
            logger?.LogDebug("TTS completed (no interruption path)");
            return new InterruptionResult(InterruptionKind.None);
        }

        logger?.LogDebug("Speaking with interruption support, {Count} quiet words configured", quietWords.Count);
        // Interruption path: do NOT suppress recognition — ListenDuringSpeech needs the live stream.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = linkedCts.Token;

        this.RaiseSpeech(ConversationSpeechSource.Spoken, text);
        var speakTask = textToSpeech.SpeakAsync(text, this.TextToSpeechOptions, linkedToken);
        var listenTask = this.ListenDuringSpeech(quietWords, linkedToken);

        var completedTask = await Task.WhenAny(speakTask, listenTask).ConfigureAwait(false);
        logger?.LogDebug("WhenAny completed - SpeakFinishedFirst: {SpeakFirst}", completedTask == speakTask);

        if (completedTask == listenTask)
        {
            var result = await listenTask.ConfigureAwait(false);
            logger?.LogDebug("Listen task completed first with result: {Kind}, Text: {Text}", result.Kind, result.Text);

            if (result.Kind != InterruptionKind.None)
            {
                logger?.LogDebug("Stopping TTS due to interruption");
                await textToSpeech.StopAsync().ConfigureAwait(false);
                await linkedCts.CancelAsync().ConfigureAwait(false);
                return result;
            }

            logger?.LogDebug("Listener ended with no interruption, waiting for TTS to finish");
            await speakTask.ConfigureAwait(false);
        }

        logger?.LogDebug("TTS finished, cancelling listener");
        await linkedCts.CancelAsync().ConfigureAwait(false);

        try { await listenTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        logger?.LogDebug("SpeakWithInterruptionSupport completed normally");
        return new InterruptionResult(InterruptionKind.None);
    }

    async Task<InterruptionResult> ListenDuringSpeech(IList<string> quietWords, CancellationToken ct)
    {
        // Match text that is ONLY a quiet word/phrase (with optional surrounding whitespace/punctuation)
        // e.g. "stop" or "shut up" but NOT "cancel this appointment"
        var quietOnlyPattern = new Regex(
            @"^\s*(" + String.Join("|", quietWords.Select(Regex.Escape)) + @")\s*[.!]?\s*$",
            RegexOptions.IgnoreCase
        );

        logger?.LogDebug("ListenDuringSpeech started, monitoring recognition stream");

        // Drain anything that arrived before TTS started to avoid stale results.
        this.DrainRecognitionChannel();

        var channel = this.recognitionChannel
            ?? throw new InvalidOperationException("Cannot listen for interruptions without an active speech session.");

        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (String.IsNullOrWhiteSpace(result.Text))
                continue;

            var trimmed = result.Text.Trim();
            logger?.LogDebug(
                "Speech detected during TTS - Text: {Text}, IsFinal: {IsFinal}, Confidence: {Confidence}",
                trimmed,
                result.IsFinal,
                result.Confidence
            );

            // Quiet-word match: the regex itself is strong evidence the user is interrupting, so
            // act on the first partial that matches without waiting for a final result or filtering
            // on confidence (iOS partials carry Confidence = 0 as a placeholder, not a real score).
            if (quietOnlyPattern.IsMatch(trimmed))
            {
                logger?.LogDebug("Quiet word matched: {Word}", trimmed);
                this.RaiseSpeech(ConversationSpeechSource.Heard, trimmed);
                return new InterruptionResult(InterruptionKind.QuietWord, trimmed, result.Confidence);
            }

            if (!result.IsFinal)
                continue;

            // For "new utterance" interruption (any non-quiet-word speech) wait for a final result,
            // then suppress likely TTS echo with the confidence floor. Confidence == 0 on a final is
            // treated as no-info rather than "very low" and is allowed through.
            if (textToSpeech.IsSpeaking
                && result.Confidence is { } conf and > 0
                && conf < this.InterruptionMinConfidence)
            {
                logger?.LogDebug(
                    "Ignoring low-confidence final during TTS - Text: {Text}, Confidence: {Confidence}",
                    trimmed,
                    conf
                );
                continue;
            }

            logger?.LogDebug("New utterance detected: {Utterance}", trimmed);
            this.RaiseSpeech(ConversationSpeechSource.Heard, trimmed);
            return new InterruptionResult(InterruptionKind.NewUtterance, trimmed, result.Confidence);
        }

        logger?.LogDebug("Recognition channel completed with no interruption");
        return new InterruptionResult(InterruptionKind.None);
    }

    public Task<IReadOnlyList<AiChatMessage>> GetChatHistory(
        string? messageContains = null,
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        int? limit = null
    )
    {
        if (messageStore == null)
            throw new InvalidOperationException("MessageStore is not set");

        return messageStore.Query(messageContains, startDate, endDate, limit);
    }

    public Task ClearChatHistory(DateTimeOffset? beforeDate = null, CancellationToken cancellationToken = default)
    {
        if (messageStore == null)
            throw new InvalidOperationException("MessageStore is not set");

        return messageStore.Clear(beforeDate);
    }

    public async Task<AccessState> RequestAccess()
    {
        var access = await speechToText.RequestAccess().ConfigureAwait(false);
        return access == AccessState.Available
            ? AccessState.Available
            : AccessState.Restricted;
    }

    public async ValueTask DisposeAsync()
    {
        await this.EndSessionAsync().ConfigureAwait(false);
        this.semaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    void SetStatus(AiState status)
    {
        logger?.LogDebug("Status changing: {OldStatus} -> {NewStatus}", this.Status, status);
        this.Status = status;
        var handler = this.StatusChanged;
        if (handler == null)
            return;

        try { handler.Invoke(status); }
        catch (Exception subscriberEx) { logger?.LogError(subscriberEx, "StatusChanged subscriber threw"); }
    }

    enum SessionMode { None, WakeWord, PushToTalk }

    async Task StartSessionAsync(SessionMode mode, string[]? keywords)
    {
        Channel<SpeechRecognitionResult> resultChannel;
        Channel<string> kwChannel;

        lock (this.sessionLock)
        {
            if (this.sessionMode != SessionMode.None)
                throw new InvalidOperationException("A conversation session is already active.");

            this.sessionMode = mode;
            this.sessionCts = new CancellationTokenSource();
            resultChannel = Channel.CreateUnbounded<SpeechRecognitionResult>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
            kwChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
            this.recognitionChannel = resultChannel;
            this.keywordChannel = kwChannel;
        }

        speechToText.ResultReceived += this.OnSpeechResult;
        speechToText.KeywordHeard += this.OnKeywordHeard;
        speechToText.Error += this.OnSpeechError;

        var options = this.SpeechToTextOptions ?? new SpeechRecognitionOptions();
        if (keywords is { Length: > 0 })
            options = options with { Keywords = keywords };

        try
        {
            await speechToText.Start(options).ConfigureAwait(false);
            this.sessionStarted = true;
            logger?.LogDebug("Speech session started (mode: {Mode}, keywords: {Keywords})", mode, keywords?.Length ?? 0);
        }
        catch
        {
            speechToText.ResultReceived -= this.OnSpeechResult;
            speechToText.KeywordHeard -= this.OnKeywordHeard;
            speechToText.Error -= this.OnSpeechError;
            lock (this.sessionLock)
            {
                this.sessionMode = SessionMode.None;
                this.sessionCts?.Dispose();
                this.sessionCts = null;
                this.recognitionChannel = null;
                this.keywordChannel = null;
            }
            throw;
        }
    }

    async Task EndSessionAsync()
    {
        CancellationTokenSource? cts;
        Channel<SpeechRecognitionResult>? resultChannel;
        Channel<string>? kwChannel;

        lock (this.sessionLock)
        {
            if (this.sessionMode == SessionMode.None)
                return;

            cts = this.sessionCts;
            resultChannel = this.recognitionChannel;
            kwChannel = this.keywordChannel;

            this.sessionMode = SessionMode.None;
            this.sessionCts = null;
            this.recognitionChannel = null;
            this.keywordChannel = null;
            this.WakeWord = null;
        }

        speechToText.ResultReceived -= this.OnSpeechResult;
        speechToText.KeywordHeard -= this.OnKeywordHeard;
        speechToText.Error -= this.OnSpeechError;

        resultChannel?.Writer.TryComplete();
        kwChannel?.Writer.TryComplete();

        try
        {
            if (cts is not null)
            {
                await cts.CancelAsync().ConfigureAwait(false);
                cts.Dispose();
            }
        }
        catch (ObjectDisposedException) { }

        try
        {
            if (this.sessionStarted)
                await speechToText.Stop().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Error stopping speech-to-text service");
        }
        finally
        {
            this.sessionStarted = false;
            Interlocked.Exchange(ref this.suppressResultsUntilTicks, 0);
            this.ClearPendingQuestions();
            this.SetStatus(AiState.Idle);
        }
    }

    void OnSpeechResult(object? sender, SpeechRecognitionResult result)
    {
        this.RaiseSpeechResult(result);

        var suppressUntil = Interlocked.Read(ref this.suppressResultsUntilTicks);
        if (suppressUntil > 0 && DateTimeOffset.UtcNow.UtcTicks < suppressUntil)
        {
            logger?.LogDebug("Suppressing recognition during TTS echo window: {Text}", result.Text);
            return;
        }

        this.recognitionChannel?.Writer.TryWrite(result);
    }

    void OnKeywordHeard(object? sender, string keyword)
    {
        this.keywordChannel?.Writer.TryWrite(keyword);
    }

    void OnSpeechError(object? sender, SpeechRecognitionError error)
    {
        logger?.LogError(error.Exception, "Speech recognition error: {Message}", error.Message);
        var ex = error.Exception ?? new InvalidOperationException(error.Message);
        this.recognitionChannel?.Writer.TryComplete(ex);
        this.keywordChannel?.Writer.TryComplete(ex);
        this.RaiseError(ex);
    }

    void RaiseError(Exception ex)
    {
        var handler = this.ErrorOccurred;
        if (handler == null)
            return;

        try { handler.Invoke(ex); }
        catch (Exception subscriberEx) { logger?.LogError(subscriberEx, "ErrorOccurred subscriber threw"); }
    }

    void RaiseSpeech(ConversationSpeechSource source, string text)
    {
        var handler = this.SpeechOccurred;
        if (handler == null)
            return;

        try { handler.Invoke(new ConversationSpeech(source, text)); }
        catch (Exception subscriberEx) { logger?.LogError(subscriberEx, "SpeechOccurred subscriber threw"); }
    }

    void RaiseAiResponded(AiResponse response)
    {
        var handler = this.AiResponded;
        if (handler == null)
            return;

        try { handler.Invoke(response); }
        catch (Exception subscriberEx) { logger?.LogError(subscriberEx, "AiResponded subscriber threw"); }
    }

    void RaiseSpeechResult(SpeechRecognitionResult result)
    {
        var handler = this.SpeechResultReceived;
        if (handler == null)
            return;

        try { handler.Invoke(result); }
        catch (Exception subscriberEx) { logger?.LogError(subscriberEx, "SpeechResultReceived subscriber threw"); }
    }

    async Task SafePlay(AiAction action)
    {
        try { await soundProvider.Play(action).ConfigureAwait(false); }
        catch (Exception ex) { logger?.LogWarning(ex, "Sound playback failed for {Action}", action); }
    }

    async Task WaitForKeywordAsync(CancellationToken ct)
    {
        var channel = this.keywordChannel
            ?? throw new InvalidOperationException("Speech session is not active.");

        // Flush stale keywords that may have arrived before the caller started waiting.
        while (channel.Reader.TryRead(out _)) { }

        await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
        channel.Reader.TryRead(out _);
    }

    /// <summary>
    /// Waits for the next final utterance. When <paramref name="timeout"/> is supplied and elapses first,
    /// returns null rather than throwing - the caller treats that as "the user didn't answer".
    /// </summary>
    async Task<string?> ReadNextUtteranceAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        var channel = this.recognitionChannel
            ?? throw new InvalidOperationException("Speech session is not active.");

        // Flush partial/non-final results that arrived before this turn started so the next final
        // result truly represents the current user utterance.
        this.DrainRecognitionChannel();

        using var timeoutCts = timeout is { } window
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;

        timeoutCts?.CancelAfter(timeout!.Value);
        var token = timeoutCts?.Token ?? ct;

        try
        {
            await foreach (var result in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (result.IsFinal)
                {
                    var text = result.Text?.Trim();
                    if (!String.IsNullOrWhiteSpace(text))
                    {
                        this.RaiseSpeech(ConversationSpeechSource.Heard, text);
                        return text;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
        {
            // The follow-up window expired, not the session - let the caller decide what that means.
            return null;
        }

        return null;
    }

    void DrainRecognitionChannel()
    {
        if (this.recognitionChannel is { } channel)
        {
            while (channel.Reader.TryRead(out _)) { }
        }
    }

    static bool TextEndsWithQuestion(string? text)
    {
        if (String.IsNullOrWhiteSpace(text))
            return false;

        var span = text.AsSpan();
        var i = span.Length - 1;

        // Skip trailing whitespace + closing markup/quotes/brackets so we can see the real terminator.
        while (i >= 0 && IsClosingMarkup(span[i]))
            i--;

        // Walk back through any run of terminator punctuation (handles "?!", "?.", etc.).
        var hasQuestion = false;
        while (i >= 0 && IsTerminator(span[i]))
        {
            if (span[i] is '?' or '？')
                hasQuestion = true;
            i--;
        }

        return hasQuestion;

        static bool IsClosingMarkup(char c) =>
            Char.IsWhiteSpace(c)
            || c is '"' or '\'' or '*' or ')' or ']' or '}' or '`' or '_' or '~'
            || c is '”' or '’';

        static bool IsTerminator(char c) =>
            c is '?' or '!' or '.' or '？' or '！' or '。';
    }
}
