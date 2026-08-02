using System.Runtime.InteropServices;

namespace Shiny.Audio;

/// <summary>
/// An ordered list of effects applied one after another to each captured buffer, plus a master
/// bypass. This is the object you hand to <see cref="AudioCaptureOptions.Effects"/> or
/// <see cref="AudioRecordingOptions.Effects"/> and then keep, so you can drive it live.
/// </summary>
/// <example>
/// <code>
/// var chain = new AudioEffectChain();
/// var pitch = chain.Add(new PitchShiftEffect { Semitones = 0 });
/// var echo  = chain.Add(new EchoEffect { Enabled = false });
///
/// await audio.Source.StartCaptureAsync(new AudioCaptureOptions { Effects = chain });
///
/// pitch.Semitones = 5;      // heard on the next buffer
/// echo.Enabled = true;
/// chain.Enabled = false;    // master bypass, crossfaded
/// </code>
/// </example>
/// <remarks>
/// Composition may change while audio is flowing: <see cref="Add{T}"/> / <see cref="Remove"/> swap
/// an immutable array atomically, and the audio thread snapshots the reference once per buffer, so
/// it always sees a complete, consistent chain — never a half-updated one.
/// </remarks>
public sealed class AudioEffectChain : AudioEffect
{
    readonly Lock gate = new();
    IAudioEffect[] effects = [];

    public AudioEffectChain()
    {
    }

    public AudioEffectChain(params IAudioEffect[] effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        this.effects = (IAudioEffect[])effects.Clone();
    }

    /// <summary>The current effects, in processing order.</summary>
    public IReadOnlyList<IAudioEffect> Effects => Volatile.Read(ref this.effects);

    /// <summary>Number of effects in the chain.</summary>
    public int Count => Volatile.Read(ref this.effects).Length;

    /// <summary>
    /// Append an effect and return it, so the caller can keep the typed reference for live control.
    /// </summary>
    public T Add<T>(T effect) where T : IAudioEffect
    {
        ArgumentNullException.ThrowIfNull(effect);
        lock (this.gate)
        {
            var updated = new IAudioEffect[this.effects.Length + 1];
            this.effects.CopyTo(updated, 0);
            updated[^1] = effect;
            Volatile.Write(ref this.effects, updated);
        }
        return effect;
    }

    /// <summary>Remove an effect. Returns false when it was not in the chain.</summary>
    public bool Remove(IAudioEffect effect)
    {
        lock (this.gate)
        {
            var index = Array.IndexOf(this.effects, effect);
            if (index < 0)
                return false;

            var updated = new IAudioEffect[this.effects.Length - 1];
            this.effects.AsSpan(0, index).CopyTo(updated);
            this.effects.AsSpan(index + 1).CopyTo(updated.AsSpan(index));
            Volatile.Write(ref this.effects, updated);
            return true;
        }
    }

    /// <summary>Remove every effect.</summary>
    public void Clear()
    {
        lock (this.gate)
            Volatile.Write(ref this.effects, []);
    }

    /// <summary>
    /// Apply the chain to a buffer of raw PCM16 bytes — the form the platform capture callbacks
    /// deliver. The byte count should be even; a trailing odd byte is left untouched.
    /// </summary>
    /// <remarks>PCM16 capture is little-endian on every platform this library supports.</remarks>
    public void Process(Span<byte> pcm, int sampleRate)
        => this.Process(MemoryMarshal.Cast<byte, short>(pcm), sampleRate);

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        // Snapshot once: Add/Remove publish a whole new array, so this loop can never observe a
        // partially-built chain.
        var snapshot = Volatile.Read(ref this.effects);
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i].Process(samples, sampleRate);
    }

    /// <inheritdoc />
    public override void Reset()
    {
        var snapshot = Volatile.Read(ref this.effects);
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i].Reset();
    }
}
