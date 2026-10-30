using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AiConversation.Infrastructure;
using Shiny.Audio;
using Shiny.Speech;

namespace Shiny.AiConversation.Voice.Infrastructure;

/// <summary>
/// One long-lived recognition loop per attached session: wake word, push-to-talk and follow-up are states of
/// it, not separate modes. Replies are spoken sentence by sentence as they stream, with the next sentence
/// synthesized while the current one plays when the engine can synthesize to a stream.
/// </summary>
public sealed class VoiceConversation : IVoiceConversation, IAudioFocusParticipant
{
    const string PushToTalkTrigger = "\0ptt";

    readonly ISpeechToTextService speechToText;
    readonly ITextToSpeechService textToSpeech;
    readonly ISoundProvider sounds;
    readonly TimeProvider time;
    readonly IAudioPlayer? player;
    readonly IWakeWordDetector? wakeWordDetector;
    readonly ILogger logger;
    readonly BroadcastChannel<VoiceEvent> events = new(e => e is InterimTranscript or AudioLevel);
    readonly Lock sync = new();
    readonly SemaphoreSlim micLock = new(1, 1);

    IAiSession? session;
    VoiceOptions? options;
    VoiceState state = VoiceState.Off;
    CancellationTokenSource? attachCts;
    CancellationTokenSource? cycleCts;
    Task loop = Task.CompletedTask;
    Channel<Heard> heard = Channel.CreateUnbounded<Heard>();
    Channel<string> triggers = Channel.CreateUnbounded<string>();
    TaskCompletionSource? resumed;
    Speaking? speaking;
    bool micOpen;
    bool paused;
    long suppressUntilTicks;
    double inputLevel;
    double outputLevel;


    public VoiceConversation(
        ISpeechToTextService speechToText,
        ITextToSpeechService textToSpeech,
        ISoundProvider sounds,
        TimeProvider time,
        IAudioPlayer? player = null,
        IWakeWordDetector? wakeWordDetector = null,
        ILogger<VoiceConversation>? logger = null
    )
    {
        this.wakeWordDetector = wakeWordDetector;
        this.speechToText = speechToText;
        this.textToSpeech = textToSpeech;
        this.sounds = sounds;
        this.time = time;
        this.player = player;
        this.logger = logger ?? NullLogger<VoiceConversation>.Instance;
    }


    public VoiceState State { get { lock (this.sync) return this.state; } }
    public IAiSession? Session { get { lock (this.sync) return this.session; } }
    public VoiceOptions? Options { get { lock (this.sync) return this.options; } }
    public TextToSpeechOptions? TextToSpeech { get; set; }


    public IAsyncEnumerable<VoiceEvent> GetEvents(CancellationToken ct = default)
        => this.events.Subscribe(() => new VoiceStateChanged(this.time.GetUtcNow(), this.State), ct);


    public Task<AccessState> RequestAccess() => this.speechToText.RequestAccess();


    public Task Attach(IAiSession session, VoiceOptions? options = null, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            if (this.session != null)
                throw new InvalidOperationException("A session is already attached; call Detach first. Only one session can use the microphone.");

            this.session = session;
            this.options = options ?? new VoiceOptions();
            this.TextToSpeech = this.options.TextToSpeech;
            this.attachCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            this.heard = Channel.CreateUnbounded<Heard>();
            this.triggers = Channel.CreateUnbounded<string>();
            this.paused = false;
        }

        this.speechToText.ResultReceived += this.OnResult;
        this.speechToText.KeywordHeard += this.OnKeyword;
        this.speechToText.Error += this.OnError;
        this.speechToText.InputLevelChanged += this.OnInputLevel;
        this.textToSpeech.AudioLevelChanged += this.OnOutputLevel;
        if (this.player != null)
            this.player.AudioLevelChanged += this.OnOutputLevel;

        var token = this.attachCts.Token;
        this.loop = Task.Run(() => this.RunLoop(token), CancellationToken.None);
        return Task.CompletedTask;
    }


    public Task PushToTalk(CancellationToken ct = default)
    {
        lock (this.sync)
        {
            if (this.session == null)
                throw new InvalidOperationException("No session is attached");

            // tapping while the AI talks stops it and listens for the user instead. Under the lock so a tap that
            // races the end of a reply is either seen by it or re-queued when it ends (never lost)
            if (this.speaking is { } current)
                current.External.TrySetResult(new Interruption(InterruptionKind.PushToTalk, null, null));
            else
                this.triggers.Writer.TryWrite(PushToTalkTrigger);
        }
        return Task.CompletedTask;
    }


    public Task StopSpeaking()
    {
        Speaking? current;
        lock (this.sync)
            current = this.speaking;

        current?.External.TrySetResult(new Interruption(InterruptionKind.Stop, null, null));
        return Task.CompletedTask;
    }


    public async Task Pause()
    {
        CancellationTokenSource? cycle;
        lock (this.sync)
        {
            if (this.session == null || this.paused)
                return;

            this.paused = true;
            this.resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cycle = this.cycleCts;
        }

        await this.StopSpeaking().ConfigureAwait(false);
        cycle?.Cancel();
        await this.CloseMic().ConfigureAwait(false);
        this.SetState(VoiceState.Paused);
    }


    public Task Resume()
    {
        TaskCompletionSource? signal;
        lock (this.sync)
        {
            this.paused = false;
            signal = this.resumed;
            this.resumed = null;
        }
        signal?.TrySetResult();
        return Task.CompletedTask;
    }


    public async Task Detach()
    {
        CancellationTokenSource? cts;
        Task running;
        lock (this.sync)
        {
            if (this.session == null)
                return;

            cts = this.attachCts;
            running = this.loop;
            this.resumed?.TrySetResult();
        }

        cts?.Cancel();
        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Voice loop ended with an error on detach");
        }

        this.speechToText.ResultReceived -= this.OnResult;
        this.speechToText.KeywordHeard -= this.OnKeyword;
        this.speechToText.Error -= this.OnError;
        this.speechToText.InputLevelChanged -= this.OnInputLevel;
        this.textToSpeech.AudioLevelChanged -= this.OnOutputLevel;
        if (this.player != null)
            this.player.AudioLevelChanged -= this.OnOutputLevel;

        await this.CloseMic().ConfigureAwait(false);
        lock (this.sync)
        {
            this.session = null;
            this.attachCts = null;
            cts?.Dispose();
        }
        this.SetState(VoiceState.Off);
    }


    public async ValueTask DisposeAsync()
    {
        await this.Detach().ConfigureAwait(false);
        this.events.Complete();
    }


    // ── the loop ────────────────────────────────────────────────────────────

    async Task RunLoop(CancellationToken attached)
    {
        (AiInput Input, string Heard)? carry = null;   // an answer or new utterance that interrupted the previous reply
        var followUp = false;      // the AI asked something
        var listenNow = false;     // push-to-talk interrupted the reply

        while (!attached.IsCancellationRequested)
        {
            CancellationTokenSource cycle;
            lock (this.sync)
            {
                this.cycleCts = CancellationTokenSource.CreateLinkedTokenSource(attached);
                cycle = this.cycleCts;
            }

            try
            {
                await this.WaitIfPaused(attached).ConfigureAwait(false);
                var opts = this.options!;
                var ct = cycle.Token;

                var utterance = carry?.Heard;
                var carried = carry?.Input;
                carry = null;

                if (utterance == null)
                {
                    if (followUp || listenNow)
                    {
                        // L5: an AI-initiated follow-up times out so room chatter isn't taken as the answer;
                        // a push-to-talk interruption is user-initiated and waits
                        var timeout = followUp && !listenNow ? opts.FollowUpTimeout : null;
                        await this.OpenMic(ct).ConfigureAwait(false);
                        this.DrainHeard();
                        this.SetState(followUp && !listenNow ? VoiceState.FollowUp : VoiceState.Listening);
                        await this.Play(AiAction.Ok).ConfigureAwait(false);
                        followUp = listenNow = false;

                        utterance = await this.ReadUtterance(timeout, ct).ConfigureAwait(false);
                        if (utterance == null)
                            continue;   // nobody answered: back to the wake word (or off)
                    }
                    else if (opts.WakeWord != null)
                    {
                        if (this.wakeWordDetector == null)
                        {
                            await this.OpenMic(ct).ConfigureAwait(false);
                            this.SetState(VoiceState.WaitingForWakeWord);
                            await this.WaitForTrigger(ct).ConfigureAwait(false);
                        }
                        else
                        {
                            // on-device spotter: the recognizer stays closed until the wake word
                            await this.CloseMic().ConfigureAwait(false);
                            this.SetState(VoiceState.WaitingForWakeWord);
                            await this.WaitForDetectedWakeWord(opts.WakeWord, ct).ConfigureAwait(false);
                            await this.OpenMic(ct).ConfigureAwait(false);
                        }

                        this.DrainHeard();
                        this.SetState(VoiceState.Listening);
                        await this.Play(AiAction.Ok).ConfigureAwait(false);
                        utterance = await this.ReadUtterance(null, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // push-to-talk only: the microphone stays closed until asked
                        await this.CloseMic().ConfigureAwait(false);
                        this.SetState(VoiceState.Off);
                        await this.WaitForTrigger(ct).ConfigureAwait(false);

                        await this.OpenMic(ct).ConfigureAwait(false);
                        this.DrainHeard();
                        this.SetState(VoiceState.Listening);
                        await this.Play(AiAction.Ok).ConfigureAwait(false);
                        utterance = await this.ReadUtterance(null, ct).ConfigureAwait(false);
                    }
                }

                if (String.IsNullOrWhiteSpace(utterance))
                    continue;

                this.Publish(new FinalTranscript(this.time.GetUtcNow(), utterance));
                var outcome = await this.Converse(carried ?? this.ToInput(utterance, this.Session?.PendingQuestions ?? []), opts, ct).ConfigureAwait(false);
                carry = outcome.Next;
                followUp = outcome.FollowUp;
                listenNow = outcome.ListenNow;
            }
            catch (OperationCanceledException) when (!attached.IsCancellationRequested)
            {
                // paused: go back to the top and wait for Resume
                carry = null;
                followUp = listenNow = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Voice loop error");
                this.Publish(new VoiceError(this.time.GetUtcNow(), ex));
                await this.Play(AiAction.Error).ConfigureAwait(false);
                followUp = listenNow = false;

                // don't spin on a broken recognizer
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), this.time, attached).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                lock (this.sync)
                {
                    if (this.cycleCts == cycle)
                        this.cycleCts = null;
                }
                cycle.Dispose();
            }
        }
    }


    async Task WaitIfPaused(CancellationToken ct)
    {
        Task? wait;
        lock (this.sync)
            wait = this.paused ? this.resumed?.Task : null;

        if (wait != null)
            await wait.WaitAsync(ct).ConfigureAwait(false);
    }


    /// <summary>
    /// Sends one utterance and speaks the reply, watching for interruptions while the model thinks and speaks.
    /// </summary>
    async Task<ConverseOutcome> Converse(AiInput input, VoiceOptions opts, CancellationToken ct)
    {
        var session = this.Session ?? throw new InvalidOperationException("No session is attached");
        if (opts.Acknowledgement == AiAcknowledgement.LessWordy)
            input = input.WithInstructions(opts.LessWordyInstruction);

        var endOfSpeech = this.time.GetTimestamp();
        this.DrainHeard();
        this.SetState(VoiceState.Thinking);
        await this.Play(AiAction.Think).ConfigureAwait(false);

        var turn = session.Send(input, CancellationToken.None);
        using var speakCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var current = new Speaking(turn);
        lock (this.sync)
            this.speaking = current;

        var interruptible = opts.Interruption != InterruptionMode.Off;
        if (!interruptible)
            Interlocked.Exchange(ref this.suppressUntilTicks, Int64.MaxValue);   // L1: mute while speaking

        var speakTask = this.SpeakReply(turn, current, opts, endOfSpeech, speakCts.Token);
        var listenTask = interruptible
            ? this.ListenForInterruption(opts, current, speakCts.Token)
            : Task.FromResult(new Interruption(InterruptionKind.None, null, null));

        try
        {
            Interruption? interruption = null;
            while (interruption == null)
            {
                var done = await Task.WhenAny(speakTask, listenTask, current.External.Task).ConfigureAwait(false);
                if (done == speakTask)
                    break;

                var candidate = done == listenTask ? await listenTask.ConfigureAwait(false) : await current.External.Task.ConfigureAwait(false);
                if (candidate.Kind != InterruptionKind.None)
                    interruption = candidate;
                else if (done == listenTask)
                    listenTask = Task.Delay(Timeout.Infinite, speakCts.Token).ContinueWith(_ => new Interruption(InterruptionKind.None, null, null), TaskScheduler.Default);
            }

            if (interruption != null)
                return await this.Interrupted(turn, current, interruption, opts, speakCts).ConfigureAwait(false);

            await speakTask.ConfigureAwait(false);
            var result = await turn.Completion.ConfigureAwait(false);

            // a tap that landed just as the reply finished must not be lost: listen now
            var lateTap = current.TryConsumeTap();
            if (result.Outcome == AiTurnOutcome.Failed)
            {
                this.Publish(new VoiceError(this.time.GetUtcNow(), result.Error ?? new InvalidOperationException("The turn failed")));
                await this.Play(AiAction.Error).ConfigureAwait(false);
                return new ConverseOutcome(null, false, lateTap);
            }
            return new ConverseOutcome(null, result.Outcome == AiTurnOutcome.AwaitingUser && !lateTap, lateTap);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // detached or paused mid-reply: record what was heard
            turn.Interrupt(current.SpokenText);
            throw;
        }
        finally
        {
            speakCts.Cancel();
            try
            {
                await listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            try
            {
                await speakTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            lock (this.sync)
            {
                this.speaking = null;
                if (current.TryConsumeTap())
                    this.triggers.Writer.TryWrite(PushToTalkTrigger);
            }

            if (!interruptible)
            {
                // L1: settle window after speaking, then drop whatever the recognizer heard of the AI's voice
                var until = this.time.GetUtcNow().Add(opts.EchoSettleWindow).UtcTicks;
                Interlocked.Exchange(ref this.suppressUntilTicks, until);
                this.DrainHeard();
            }
        }
    }


    async Task<ConverseOutcome> Interrupted(IAiTurn turn, Speaking current, Interruption interruption, VoiceOptions opts, CancellationTokenSource speakCts)
    {
        speakCts.Cancel();
        await this.StopAudio().ConfigureAwait(false);

        // P4: the model is told what was actually heard
        var spoken = current.SpokenText;
        turn.Interrupt(spoken);
        var reason = interruption.Kind switch
        {
            InterruptionKind.QuietWord => InterruptionReason.QuietWord,
            InterruptionKind.Answer => InterruptionReason.Answer,
            InterruptionKind.NewUtterance => InterruptionReason.NewSpeech,
            InterruptionKind.PushToTalk => InterruptionReason.PushToTalk,
            _ => InterruptionReason.Stop
        };
        this.Publish(new SpeechInterrupted(this.time.GetUtcNow(), reason, interruption.Heard, spoken));

        switch (interruption.Kind)
        {
            case InterruptionKind.NewUtterance:
            case InterruptionKind.Answer:
                return new ConverseOutcome((interruption.Input!, interruption.Heard!), false, false);

            case InterruptionKind.PushToTalk:
                current.TryConsumeTap();
                return new ConverseOutcome(null, false, true);

            default:
                await this.Play(AiAction.Cancel).ConfigureAwait(false);
                return default;
        }
    }


    /// <summary>
    /// What a heard utterance sends: a structured answer when it is entirely one of the pending question's
    /// choices (e.g. answering a follow-up with "the second one"), otherwise the words themselves.
    /// </summary>
    AiInput ToInput(string utterance, IReadOnlyList<AiQuestion> pending)
    {
        if (this.options?.AnswerInterruptions != false && AnswerMatcher.Match(utterance, pending) is { } answer)
            return AnswerInput(answer.Question, answer.ChoiceIds);

        return AiInput.Voice(utterance);
    }


    /// <summary>
    /// A choice answer that was spoken: structured for the model, but voice-sourced so the reply stays speakable.
    /// </summary>
    static AiInput AnswerInput(AiQuestion question, IReadOnlyList<string> choiceIds)
        => AiInput.Answer(question, [.. choiceIds]) with { Source = MessageSource.Voice };


    // ── speaking ────────────────────────────────────────────────────────────

    async Task SpeakReply(IAiTurn turn, Speaking current, VoiceOptions opts, long endOfSpeech, CancellationToken ct)
    {
        if (opts.Acknowledgement < AiAcknowledgement.LessWordy)
        {
            // no text-to-speech: cue the reply and wait for the turn to end
            var cued = false;
            await foreach (var e in turn.Events.WithCancellation(ct).ConfigureAwait(false))
            {
                current.Observe(e);
                if (e is TextDelta && !cued && opts.Acknowledgement == AiAcknowledgement.AudioBlip)
                {
                    cued = true;
                    await this.Play(AiAction.Respond).ConfigureAwait(false);
                }
            }
            return;
        }

        var sentences = Channel.CreateUnbounded<Utterance>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = this.ProduceSentences(turn, current, opts, sentences.Writer, ct);
        try
        {
            if (this.textToSpeech.CanSynthesizeToStream && this.player != null)
                await this.SpeakPrefetched(sentences.Reader, current, endOfSpeech, ct).ConfigureAwait(false);
            else
                await this.SpeakSequential(sentences.Reader, current, endOfSpeech, ct).ConfigureAwait(false);
        }
        finally
        {
            await producer.ConfigureAwait(false);
        }
    }


    async Task ProduceSentences(IAiTurn turn, Speaking current, VoiceOptions opts, ChannelWriter<Utterance> writer, CancellationToken ct)
    {
        var chunker = new SentenceChunker();
        var anyText = false;
        var acknowledged = false;
        try
        {
            await foreach (var e in turn.Events.WithCancellation(ct).ConfigureAwait(false))
            {
                current.Observe(e);
                switch (e)
                {
                    case TextDelta d:
                        anyText = true;
                        foreach (var s in chunker.Add(d.Text))
                            Write(s, true);
                        break;

                    // silence while a tool runs can sound like a hang; silent tools (memory, search) never say anything
                    case ToolCallStarted { Silent: false } when !anyText && !acknowledged && opts.ToolAcknowledgement != null:
                        acknowledged = true;
                        Write(opts.ToolAcknowledgement, false);
                        break;
                }
            }
            foreach (var s in chunker.Flush())
                Write(s, true);

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex is OperationCanceledException ? null : ex);
        }

        void Write(string text, bool isReply)
        {
            var speakable = SpeechText.StripMarkdown(text);
            if (speakable.Any(Char.IsLetterOrDigit))
                writer.TryWrite(new Utterance(speakable, isReply));
        }
    }


    async Task SpeakSequential(ChannelReader<Utterance> reader, Speaking current, long endOfSpeech, CancellationToken ct)
    {
        await foreach (var u in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            this.BeforeSentence(u, current, endOfSpeech);
            await OffEngineThread(this.textToSpeech.SpeakAsync(u.Text, this.TextToSpeech, ct)).ConfigureAwait(false);
            current.Spoke(u);
        }
    }


    /// <summary>
    /// Synthesizes the next sentence while the current one plays, so cloud voices don't pause between sentences.
    /// </summary>
    async Task SpeakPrefetched(ChannelReader<Utterance> reader, Speaking current, long endOfSpeech, CancellationToken ct)
    {
        var synthesized = Channel.CreateBounded<(Utterance Utterance, Task<Stream> Audio)>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        var synth = Task.Run(async () =>
        {
            try
            {
                await foreach (var u in reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await synthesized.Writer.WriteAsync((u, this.textToSpeech.SynthesizeToStreamAsync(u.Text, this.TextToSpeech, ct)), ct).ConfigureAwait(false);

                synthesized.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                synthesized.Writer.TryComplete(ex is OperationCanceledException ? null : ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var (u, audio) in synthesized.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await using var stream = await OffEngineThread(audio).ConfigureAwait(false);
                this.BeforeSentence(u, current, endOfSpeech);

                await using var playback = await this.player!.StartAsync(stream, ct).ConfigureAwait(false);
                await OffEngineThread(playback.Completion.WaitAsync(ct)).ConfigureAwait(false);
                current.Spoke(u);
            }
        }
        finally
        {
            await synth.ConfigureAwait(false);
        }
    }


    void BeforeSentence(Utterance u, Speaking current, long endOfSpeech)
    {
        if (current.MarkFirstAudio())
            this.Publish(new ResponseLatency(this.time.GetUtcNow(), this.time.GetElapsedTime(endOfSpeech)));

        this.SetState(VoiceState.Speaking);
        this.Publish(new SpeakingSentence(this.time.GetUtcNow(), u.Text));
    }


    async Task StopAudio()
    {
        try
        {
            await OffEngineThread(this.textToSpeech.StopAsync()).ConfigureAwait(false);
            if (this.player != null)
                await OffEngineThread(this.player.StopAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Stopping audio failed");
        }
    }


    /// <summary>
    /// Engines often complete their tasks from a native audio callback (iOS AVAudioPlayerNode, Android
    /// UtteranceProgressListener…) and some run continuations inline there. Our next call - which may stop that
    /// engine's player - must never run on its callback queue: iOS traps AVAudioPlayerNode.Stop() on its own queue.
    /// Engines are pluggable, so the driver hops to the thread pool itself rather than trusting each one.
    /// (ConfigureAwaitOptions.ForceYielding doesn't help: it only applies to already-completed tasks.)
    /// </summary>
    static async Task OffEngineThread(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            await Task.Yield();
        }
    }

    static async Task<T> OffEngineThread<T>(Task<T> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            await Task.Yield();
        }
    }


    // ── listening ───────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for the next final utterance; a timeout returns null. Callers drain stale results (L4) before
    /// announcing that they're listening, so nothing said after that point is lost.
    /// </summary>
    async Task<string?> ReadUtterance(TimeSpan? timeout, CancellationToken ct)
    {
        using var timeoutCts = timeout is { } window ? new CancellationTokenSource(window, this.time) : null;
        using var linked = timeoutCts == null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked?.Token ?? ct;

        try
        {
            await foreach (var item in this.heard.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (item.Failed)
                    return null;

                if (item.Result is { IsFinal: true } r && !String.IsNullOrWhiteSpace(r.Text))
                    return r.Text.Trim();
            }
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
        }
        return null;
    }


    async Task WaitForDetectedWakeWord(string wakeWord, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var detected = Task.Run(async () =>
        {
            await foreach (var _ in this.wakeWordDetector!.Listen([wakeWord], stop.Token).ConfigureAwait(false))
                return;
        }, CancellationToken.None);
        var tapped = this.WaitForTrigger(stop.Token);

        var first = await Task.WhenAny(detected, tapped).ConfigureAwait(false);
        stop.Cancel();
        await first.ConfigureAwait(false);   // surfaces cancellation / detector failure
        try
        {
            await Task.WhenAll(detected, tapped).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }


    async Task WaitForTrigger(CancellationToken ct)
    {
        // L4: a wake word heard before we started waiting is stale; a push-to-talk tap is deliberate and never dropped
        while (this.triggers.Reader.TryRead(out var stale))
        {
            if (stale == PushToTalkTrigger)
                return;
        }
        await this.triggers.Reader.ReadAsync(ct).ConfigureAwait(false);
    }


    /// <summary>
    /// While the AI thinks and speaks: L2 a quiet word acts on the first partial that is entirely the quiet
    /// word (no confidence filter; iOS partials report 0); in Full mode a final utterance interrupts, with
    /// L3 the confidence floor applied only while audio is playing, and 0 treated as "not reported".
    /// </summary>
    async Task<Interruption> ListenForInterruption(VoiceOptions opts, Speaking current, CancellationToken ct)
    {
        await this.OpenMic(ct).ConfigureAwait(false);
        var quietWords = opts.QuietWords.Count == 0
            ? null
            : new Regex(@"^\s*(" + String.Join("|", opts.QuietWords.Select(Regex.Escape)) + @")\s*[.!]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        await foreach (var item in this.heard.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (item.Result is not { } r || String.IsNullOrWhiteSpace(r.Text))
                continue;

            var text = r.Text.Trim();
            if (quietWords?.IsMatch(text) == true)
                return new Interruption(InterruptionKind.QuietWord, text, null);

            if (!r.IsFinal)
                continue;

            if (current.HasStartedAudio && r.Confidence is { } confidence and > 0 && confidence < opts.InterruptionMinConfidence)
                continue;   // probably the AI's own voice

            // answering the question being asked: a final that is entirely one of its choices
            if (opts.AnswerInterruptions && AnswerMatcher.Match(text, current.Questions) is { } answer)
                return new Interruption(InterruptionKind.Answer, text, AnswerInput(answer.Question, answer.ChoiceIds));

            if (opts.Interruption == InterruptionMode.Full)
                return new Interruption(InterruptionKind.NewUtterance, text, AiInput.Voice(text));
        }
        return new Interruption(InterruptionKind.None, null, null);
    }


    async Task OpenMic(CancellationToken ct)
    {
        await this.micLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.micOpen && this.speechToText.IsListening)
                return;

            await this.speechToText.Start(this.BuildRecognitionOptions()).ConfigureAwait(false);
            this.micOpen = true;
        }
        finally
        {
            this.micLock.Release();
        }
    }


    async Task CloseMic()
    {
        await this.micLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!this.micOpen)
                return;

            this.micOpen = false;
            await this.speechToText.Stop().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Stopping speech-to-text failed");
        }
        finally
        {
            this.micLock.Release();
        }
    }


    SpeechRecognitionOptions BuildRecognitionOptions()
    {
        var opts = this.options ?? new VoiceOptions();
        var recognition = opts.SpeechToText ?? new SpeechRecognitionOptions();

        if (opts.WakeWord != null && this.wakeWordDetector == null)
            recognition = recognition with { Keywords = [.. recognition.Keywords ?? [], opts.WakeWord] };

        // D15: listening while speaking needs echo cancellation
        if (opts.Interruption != InterruptionMode.Off)
            recognition = recognition with { AudioProcessing = (recognition.AudioProcessing ?? AudioProcessingOptions.VoiceChat) with { EchoCancellation = true } };

        return recognition;
    }


    void DrainHeard()
    {
        while (this.heard.Reader.TryRead(out _)) { }
    }


    // ── speech events ───────────────────────────────────────────────────────

    void OnResult(object? sender, SpeechRecognitionResult result)
    {
        if (!result.IsFinal && !String.IsNullOrWhiteSpace(result.Text))
            this.Publish(new InterimTranscript(this.time.GetUtcNow(), result.Text));

        var suppressUntil = Interlocked.Read(ref this.suppressUntilTicks);
        if (suppressUntil > 0 && this.time.GetUtcNow().UtcTicks < suppressUntil)
            return;

        this.heard.Writer.TryWrite(new Heard(result, false));
    }


    void OnKeyword(object? sender, string keyword) => this.triggers.Writer.TryWrite(keyword);


    void OnError(object? sender, SpeechRecognitionError error)
    {
        var ex = error.Exception ?? new InvalidOperationException(error.Message);
        this.logger.LogError(ex, "Speech recognition error: {Message}", error.Message);
        this.Publish(new VoiceError(this.time.GetUtcNow(), ex));

        // let a pending read give up; the next listen restarts the recognizer if it stopped
        if (!this.speechToText.IsListening)
            this.micOpen = false;

        this.heard.Writer.TryWrite(new Heard(null, true));
    }


    void OnInputLevel(object? sender, double level)
    {
        this.inputLevel = level;
        this.Publish(new AudioLevel(this.time.GetUtcNow(), level, this.outputLevel));
    }


    void OnOutputLevel(object? sender, double level)
    {
        this.outputLevel = level;
        this.Publish(new AudioLevel(this.time.GetUtcNow(), this.inputLevel, level));
    }


    void SetState(VoiceState newState)
    {
        lock (this.sync)
        {
            if (this.state == newState)
                return;

            this.state = newState;
        }
        this.Publish(new VoiceStateChanged(this.time.GetUtcNow(), newState));
    }


    void Publish(VoiceEvent e) => this.events.Publish(e);


    async Task Play(AiAction action)
    {
        if (this.options?.Acknowledgement == AiAcknowledgement.None)
            return;

        try
        {
            await OffEngineThread(this.sounds.Play(action)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Sound playback failed for {Action}", action);   // L9
        }
    }


    // ── state for one reply ─────────────────────────────────────────────────

    enum InterruptionKind { None, QuietWord, Answer, NewUtterance, PushToTalk, Stop }

    record Interruption(InterruptionKind Kind, string? Heard, AiInput? Input);

    record struct ConverseOutcome((AiInput Input, string Heard)? Next, bool FollowUp, bool ListenNow);

    record Heard(SpeechRecognitionResult? Result, bool Failed);

    record Utterance(string Text, bool IsReply);

    sealed class Speaking(IAiTurn turn)
    {
        readonly Lock sync = new();
        readonly StringBuilder spoken = new();
        bool firstAudio;
        bool tapConsumed;
        IReadOnlyList<AiQuestion> questions = [];

        public IAiTurn Turn => turn;

        /// <summary>
        /// What the reply is asking, known as soon as the ask_user call streams in (usually while still speaking).
        /// </summary>
        public IReadOnlyList<AiQuestion> Questions
        {
            get { lock (this.sync) return this.questions; }
        }

        public void Observe(AiTurnEvent e)
        {
            IReadOnlyList<AiQuestion>? found = e switch
            {
                QuestionsAsked q => q.Questions,
                ToolCallStarted { Name: AskUserFunction.ToolName } call => ParseAskUser(call.ArgumentsJson),
                _ => null
            };
            if (found is { Count: > 0 })
            {
                lock (this.sync)
                    this.questions = found;
            }
        }

        static IReadOnlyList<AiQuestion>? ParseAskUser(string argumentsJson)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
                var args = new Dictionary<string, object?>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    args[prop.Name] = prop.Value.Clone();

                return AskUserFunction.Parse(args);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// True (once) when a push-to-talk tap arrived for this reply and nobody has acted on it yet.
        /// </summary>
        public bool TryConsumeTap()
        {
            lock (this.sync)
            {
                if (this.tapConsumed || !this.External.Task.IsCompletedSuccessfully || this.External.Task.Result.Kind != InterruptionKind.PushToTalk)
                    return false;

                this.tapConsumed = true;
                return true;
            }
        }
        public TaskCompletionSource<Interruption> External { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasStartedAudio
        {
            get { lock (this.sync) return this.firstAudio; }
        }

        /// <summary>
        /// The reply text that has been fully spoken (acknowledgements excluded).
        /// </summary>
        public string SpokenText
        {
            get { lock (this.sync) return this.spoken.ToString(); }
        }

        public bool MarkFirstAudio()
        {
            lock (this.sync)
            {
                if (this.firstAudio)
                    return false;

                this.firstAudio = true;
                return true;
            }
        }

        public void Spoke(Utterance u)
        {
            if (!u.IsReply)
                return;

            lock (this.sync)
            {
                if (this.spoken.Length > 0)
                    this.spoken.Append(' ');

                this.spoken.Append(u.Text);
            }
        }
    }
}
