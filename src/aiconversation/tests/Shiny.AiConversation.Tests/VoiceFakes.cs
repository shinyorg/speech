using System.Globalization;
using Shiny.AiConversation.Infrastructure;
using Shiny.AiConversation.Voice;
using Shiny.AiConversation.Voice.Infrastructure;
using Shiny.Audio;
using Shiny.Speech;

namespace Shiny.AiConversation.Tests;

public class FakeSpeechToText : ISpeechToTextService
{
    public List<SpeechRecognitionOptions?> Starts { get; } = [];
    public int Stops { get; private set; }
    public AccessState Access { get; set; } = AccessState.Available;

    public bool IsSupported => true;
    public bool IsListening { get; private set; }
    public bool IsInputAnalysisSupported => true;

    public event EventHandler<SpeechRecognitionResult>? ResultReceived;
    public event EventHandler<string>? KeywordHeard;
    public event EventHandler<SpeechRecognitionError>? Error;
    public event EventHandler<double>? InputLevelChanged;

    public Task<AccessState> RequestAccess() => Task.FromResult(this.Access);

    public Task Start(SpeechRecognitionOptions? options = null)
    {
        this.Starts.Add(options);
        this.IsListening = true;
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        this.Stops++;
        this.IsListening = false;
        return Task.CompletedTask;
    }

    public void Partial(string text, float? confidence = 0) => this.ResultReceived?.Invoke(this, new SpeechRecognitionResult(text, false, confidence));
    public void Final(string text, float? confidence = null) => this.ResultReceived?.Invoke(this, new SpeechRecognitionResult(text, true, confidence));
    public void Keyword(string keyword) => this.KeywordHeard?.Invoke(this, keyword);
    public void Fail(string message) => this.Error?.Invoke(this, new SpeechRecognitionError(message));
    public void Level(double level) => this.InputLevelChanged?.Invoke(this, level);
}


public class FakeTextToSpeech : ITextToSpeechService
{
    readonly Lock sync = new();
    TaskCompletionSource? gate;

    public List<string> Spoken { get; } = [];
    public List<string> Synthesized { get; } = [];
    public List<TextToSpeechOptions?> Options { get; } = [];
    public int StopCalls { get; private set; }
    public bool CanSynthesizeToStream { get; set; }
    public List<VoiceInfo> Voices { get; } = [new("v1", "Ava", new CultureInfo("en-US")), new("v2", "Liam", new CultureInfo("en-GB"))];

    public bool IsSupported => true;
    public bool IsSpeaking { get; private set; }
    public bool IsPlayerAnalysisSupported => false;
    public event EventHandler<double>? AudioLevelChanged;

    /// <summary>
    /// Speech blocks until <see cref="Release"/> (or cancellation) once this is set.
    /// </summary>
    public void Hold()
    {
        lock (this.sync)
            this.gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release()
    {
        lock (this.sync)
        {
            this.gate?.TrySetResult();
            this.gate = null;
        }
    }

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CultureInfo? culture = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<VoiceInfo>>(this.Voices.Where(v => culture == null || v.Culture.Name == culture.Name).ToList());

    public async Task SpeakAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        Task wait;
        lock (this.sync)
        {
            this.Spoken.Add(text);
            this.Options.Add(options);
            wait = this.gate?.Task ?? Task.CompletedTask;
        }

        this.IsSpeaking = true;
        try
        {
            await wait.WaitAsync(cancellationToken);
            await Task.Yield();
        }
        finally
        {
            this.IsSpeaking = false;
        }
    }

    public Task StopAsync()
    {
        this.StopCalls++;
        return Task.CompletedTask;
    }

    public Task<Stream> SynthesizeToStreamAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (this.sync)
            this.Synthesized.Add(text);

        return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    public void RaiseLevel(double level) => this.AudioLevelChanged?.Invoke(this, level);
}


#pragma warning disable CS0067   // interface events the fake never raises
public class FakeAudioPlayer : IAudioPlayer
{
    public List<string> Played { get; } = [];
    public int StopCalls { get; private set; }

    public IReadOnlyList<IAudioPlayback> Active => [];
    public bool IsPlaying => false;
    public bool IsPlayerAnalysisSupported => false;
    public bool IsVolumeControlSupported => false;
    public float Volume { get; set; } = 1;
    public event EventHandler<double>? AudioLevelChanged;
    public event EventHandler<float>? VolumeChanged;

    public async Task<IAudioPlayback> StartAsync(Stream audioStream, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(audioStream);
        this.Played.Add(await reader.ReadToEndAsync(cancellationToken));
        return new Playback();
    }

    public Task StopAsync()
    {
        this.StopCalls++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    sealed class Playback : IAudioPlayback
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string? Source => null;
        public bool IsPlaying => false;
        public Task Completion => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}


#pragma warning restore CS0067


public class FakeWakeWordDetector : IWakeWordDetector
{
    readonly System.Threading.Channels.Channel<string> heard = System.Threading.Channels.Channel.CreateUnbounded<string>();
    public int Listens { get; private set; }

    public void Hear(string word) => this.heard.Writer.TryWrite(word);

    public async IAsyncEnumerable<string> Listen(IReadOnlyList<string> wakeWords, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        this.Listens++;
        while (true)
            yield return await this.heard.Reader.ReadAsync(ct);
    }
}


public class FakeSounds : ISoundProvider
{
    readonly Lock sync = new();
    readonly List<AiAction> played = [];

    public IReadOnlyList<AiAction> Played
    {
        get { lock (this.sync) return this.played.ToList(); }
    }

    public Task Play(AiAction action)
    {
        lock (this.sync)
            this.played.Add(action);

        return Task.CompletedTask;
    }
}


public sealed class VoiceHarness : IAsyncDisposable
{
    public FakeChatClient Chat { get; } = new();
    public FakeSpeechToText Stt { get; } = new();
    public FakeTextToSpeech Tts { get; } = new();
    public FakeSounds Sounds { get; } = new();
    public FakeAudioPlayer? Player { get; }
    public AiSessionManager Manager { get; }
    public VoiceConversation Voice { get; }
    public IAiSession Session { get; private set; } = null!;
    public EventCollector SessionEvents { get; private set; } = null!;

    readonly List<VoiceEvent> voiceEvents = [];
    readonly Lock sync = new();
    readonly CancellationTokenSource cts = new();
    Task eventLoop = Task.CompletedTask;

    public FakeWakeWordDetector? Detector { get; }

    public VoiceHarness(bool withPlayer = false, params ISessionInterceptor[] interceptors) : this(withPlayer, null, interceptors) { }

    public VoiceHarness(bool withPlayer, FakeWakeWordDetector? detector, params ISessionInterceptor[] interceptors)
    {
        this.Detector = detector;
        this.Player = withPlayer ? new FakeAudioPlayer() : null;
        this.Manager = new AiSessionManager(
            new InMemoryAiSessionStore(),
            new FakeChatClientProvider(this.Chat),
            interceptors,
            TimeProvider.System,
            memoryOptions: new AiMemoryOptions { Enabled = false }
        );
        this.Voice = new VoiceConversation(this.Stt, this.Tts, this.Sounds, TimeProvider.System, this.Player, detector);
    }

    public IReadOnlyList<VoiceEvent> VoiceEvents
    {
        get { lock (this.sync) return this.voiceEvents.ToList(); }
    }

    public async Task Start(VoiceOptions? options = null)
    {
        this.Session = await this.Manager.Start();
        this.SessionEvents = await EventCollector.Start(this.Session);

        var e = this.Voice.GetEvents(this.cts.Token).GetAsyncEnumerator();
        await e.MoveNextAsync();
        this.eventLoop = Task.Run(async () =>
        {
            try
            {
                while (await e.MoveNextAsync())
                {
                    lock (this.sync)
                        this.voiceEvents.Add(e.Current);
                }
            }
            finally
            {
                await e.DisposeAsync();
            }
        });

        await this.Voice.Attach(this.Session, options ?? new VoiceOptions());
    }

    public async Task WaitForState(VoiceState state, int timeoutMs = 5000)
    {
        try
        {
            await WaitUntil(() => this.Voice.State == state, timeoutMs, $"voice state {state} (is {this.Voice.State})");
        }
        catch (TimeoutException ex)
        {
            var history = String.Join(" > ", this.VoiceEvents.Where(e => e is not Shiny.AiConversation.Voice.AudioLevel).Select(e => e switch
            {
                VoiceStateChanged s => s.State.ToString(),
                SpeakingSentence s => $"say({s.Text})",
                SpeechInterrupted i => $"interrupted({i.HeardText})",
                FinalTranscript t => $"heard({t.Text})",
                _ => e.GetType().Name
            }));
            throw new TimeoutException($"Timed out waiting for voice state {state} (now {this.Voice.State}); events: {history}", ex);
        }
    }

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Timed out waiting for " + (what ?? "condition"));

            await Task.Delay(5);
        }
    }

    public async Task<List<AiMessageDocument>> Messages()
        => (await this.Session.GetMessages(null, 100)).Messages.ToList();

    public async ValueTask DisposeAsync()
    {
        await this.Voice.DisposeAsync();
        this.cts.Cancel();
        await this.eventLoop;
        await this.SessionEvents.DisposeAsync();
        await this.Session.DisposeAsync();
    }
}


/// <summary>
/// Mimics iOS AVSpeech/AVAudioPlayerNode: speech completes from a native callback thread with a TCS that runs
/// continuations inline, and calling back into the engine from inside that callback is a fatal error.
/// </summary>
public class CallbackTextToSpeech : ITextToSpeechService
{
    [ThreadStatic] static bool inCallback;

    public List<string> Spoken { get; } = [];
    public List<string> ReentrantCalls { get; } = [];

    public bool IsSupported => true;
    public bool IsSpeaking => false;
    public bool CanSynthesizeToStream => false;
    public bool IsPlayerAnalysisSupported => false;
#pragma warning disable CS0067
    public event EventHandler<double>? AudioLevelChanged;
#pragma warning restore CS0067

    public Task<IReadOnlyList<System.Globalization.CultureInfo>> Unused() => throw new NotSupportedException();
    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(System.Globalization.CultureInfo? culture = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<VoiceInfo>>([]);

    public Task SpeakAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
    {
        this.CheckReentry(nameof(SpeakAsync));
        lock (this.Spoken)
            this.Spoken.Add(text);

        var tcs = new TaskCompletionSource();   // deliberately inline continuations, like the old engine
        var callback = new Thread(() =>
        {
            Thread.Sleep(5);
            inCallback = true;
            try
            {
                tcs.TrySetResult();
            }
            finally
            {
                inCallback = false;
            }
        }) { IsBackground = true };
        callback.Start();
        return tcs.Task;
    }

    public Task StopAsync()
    {
        this.CheckReentry(nameof(StopAsync));
        return Task.CompletedTask;
    }

    public Task<Stream> SynthesizeToStreamAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    void CheckReentry(string call)
    {
        if (inCallback)
            lock (this.ReentrantCalls)
                this.ReentrantCalls.Add(call);
    }
}
