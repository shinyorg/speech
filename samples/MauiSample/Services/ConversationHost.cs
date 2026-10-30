using Shiny.AiConversation;
using Shiny.AiConversation.Voice;

namespace MauiSample.Services;

/// <summary>
/// Owns the one session the whole app talks to, so the chat page and the voice (Aura) page share a single
/// instance: the recommended way to avoid two instances of one session racing each other.
/// </summary>
public class ConversationHost(IAiSessionManager manager, IVoiceConversation voice)
{
    readonly SemaphoreSlim gate = new(1, 1);
    IAiSession? session;

    public IVoiceConversation Voice => voice;
    public IAiSessionManager Manager => manager;

    public VoiceOptions VoiceOptions { get; private set; } = new()
    {
        Interruption = InterruptionMode.QuietWords,
        Acknowledgement = AiAcknowledgement.Full
    };

    /// <summary>Raised (off the UI thread) when a different session becomes current.</summary>
    public event EventHandler<IAiSession>? SessionChanged;


    /// <summary>The current session: the most recently active one, or a new one.</summary>
    public async Task<IAiSession> GetSession()
    {
        await this.gate.WaitAsync();
        try
        {
            this.session ??= await manager.GetLatest() ?? await manager.Start();
            return this.session;
        }
        finally
        {
            this.gate.Release();
        }
    }


    public async Task<IAiSession> NewConversation()
    {
        IAiSession next;
        await this.gate.WaitAsync();
        try
        {
            next = await this.Replace(await manager.Start());
        }
        finally
        {
            this.gate.Release();
        }

        this.SessionChanged?.Invoke(this, next);
        return next;
    }


    /// <summary>Deletes every stored conversation and starts a fresh one.</summary>
    public async Task DeleteAll()
    {
        IAiSession next;
        await this.gate.WaitAsync();
        try
        {
            await voice.Detach();
            var all = await manager.Query(new SessionQuery(Take: 1000));
            foreach (var s in all)
                await manager.Delete(s.SessionId);

            next = await this.Replace(await manager.Start());
        }
        finally
        {
            this.gate.Release();
        }

        this.SessionChanged?.Invoke(this, next);
    }


    /// <summary>Attaches the voice conversation to the current session (re-attaching when options changed).</summary>
    public async Task EnsureVoice()
    {
        var current = await this.GetSession();
        if (ReferenceEquals(voice.Session, current) && voice.Options == this.VoiceOptions)
            return;

        await voice.Detach();
        await voice.Attach(current, this.VoiceOptions);
    }


    public async Task UpdateVoice(Func<VoiceOptions, VoiceOptions> change)
    {
        this.VoiceOptions = change(this.VoiceOptions);
        if (voice.Session != null)
            await this.EnsureVoice();
    }


    async Task<IAiSession> Replace(IAiSession next)
    {
        var old = this.session;
        this.session = next;

        if (old != null)
        {
            var wasAttached = ReferenceEquals(voice.Session, old);
            if (wasAttached)
                await voice.Detach();

            await old.DisposeAsync();
            if (wasAttached)
                await voice.Attach(next, this.VoiceOptions);
        }
        return next;
    }
}
