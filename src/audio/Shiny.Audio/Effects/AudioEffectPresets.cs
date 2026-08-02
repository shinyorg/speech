namespace Shiny.Audio;

/// <summary>Ready-made effect combinations.</summary>
public enum AudioEffectPreset
{
    /// <summary>Metallic monotone — ring modulation with a little grit.</summary>
    Robot,

    /// <summary>Pitched well up, for a small squeaky voice.</summary>
    Chipmunk,

    /// <summary>Pitched down for a low, menacing voice.</summary>
    DeepVoice,

    /// <summary>Long, dark reverb — a large stone room.</summary>
    Cathedral,

    /// <summary>Narrow band-passed voice, like a phone line or intercom.</summary>
    Telephone,

    /// <summary>Distorted and band-limited, like a loud-hailer.</summary>
    Megaphone,

    /// <summary>Doubled and detuned, as though several people were speaking together.</summary>
    Ensemble
}

/// <summary>Factories for the built-in <see cref="AudioEffectPreset"/> combinations.</summary>
public static class AudioEffectPresets
{
    /// <summary>
    /// Build a chain for a preset. The returned chain and its effects are ordinary mutable objects,
    /// so a preset is a starting point you can adjust live rather than a fixed mode.
    /// </summary>
    /// <example>
    /// <code>
    /// var chain = AudioEffectPresets.Create(AudioEffectPreset.Robot);
    /// var ring = chain.Effects.OfType&lt;RingModEffect&gt;().First();
    /// ring.Frequency = 80f;   // tune it while recording
    /// </code>
    /// </example>
    public static AudioEffectChain Create(AudioEffectPreset preset)
    {
        var chain = new AudioEffectChain();

        switch (preset)
        {
            case AudioEffectPreset.Robot:
                chain.Add(new RingModEffect { Frequency = 45f, Mix = 0.9f });
                chain.Add(new DistortionEffect { Drive = 4f, Mix = 0.35f });
                break;

            case AudioEffectPreset.Chipmunk:
                chain.Add(new PitchShiftEffect { Semitones = 7f });
                break;

            case AudioEffectPreset.DeepVoice:
                chain.Add(new PitchShiftEffect { Semitones = -6f });
                chain.Add(new BiquadFilterEffect { Type = BiquadFilterType.LowPass, Frequency = 3000f });
                break;

            case AudioEffectPreset.Cathedral:
                chain.Add(new ReverbEffect { RoomSize = 0.95f, Damping = 0.25f, Mix = 0.55f });
                break;

            case AudioEffectPreset.Telephone:
                chain.Add(BiquadFilterEffect.Telephone());
                chain.Add(new DistortionEffect { Drive = 3f, Mix = 0.2f });
                break;

            case AudioEffectPreset.Megaphone:
                chain.Add(new BiquadFilterEffect { Type = BiquadFilterType.BandPass, Frequency = 1500f, Q = 1.2f });
                chain.Add(new DistortionEffect { Drive = 14f, Mix = 0.8f });
                chain.Add(new EchoEffect { DelayMs = 90f, Feedback = 0.2f, Mix = 0.2f });
                break;

            case AudioEffectPreset.Ensemble:
                chain.Add(new ChorusEffect { RateHz = 0.5f, DepthMs = 8f, Mix = 0.6f });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preset.");
        }

        return chain;
    }
}
