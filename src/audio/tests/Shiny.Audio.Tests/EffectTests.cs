using Shiny.Audio;

namespace Shiny.Audio.Tests;

public class GainEffectTests
{
    [Test]
    public async Task Unity_LeavesSignalAlone()
    {
        var input = SignalMath.Sine(440, 4000);
        var output = SignalMath.Run(new GainEffect { Gain = 1f }, input);

        // Allow one unit of rounding from the float round-trip.
        for (var i = 0; i < input.Length; i++)
            await Assert.That(Math.Abs(output[i] - input[i])).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task Halving_HalvesTheLevel()
    {
        var input = SignalMath.Sine(440, 8000, amplitude: 6000);
        var output = SignalMath.Run(new GainEffect { Gain = 0.5f }, input);

        // Skip the smoothing ramp at the start.
        var ratio = SignalMath.Rms(output.AsSpan(2000)) / SignalMath.Rms(input.AsSpan(2000));
        await Assert.That(ratio).IsEqualTo(0.5).Within(0.02);
    }

    [Test]
    public async Task GainDb_RoundTrips()
    {
        var effect = new GainEffect { GainDb = -6f };
        await Assert.That(effect.Gain).IsEqualTo(0.501f).Within(0.01f);
        await Assert.That(effect.GainDb).IsEqualTo(-6f).Within(0.01f);
    }

    [Test]
    public async Task HeavyBoost_SoftClipsInsteadOfWrapping()
    {
        var input = SignalMath.Sine(300, 8000, amplitude: 20000);
        var output = SignalMath.Run(new GainEffect { Gain = 8f }, input);

        // The failure this guards against is integer wraparound turning a loud positive peak into a
        // full-scale negative spike, which sounds like a gunshot. Saturating at the rail is correct;
        // flipping sign is not.
        await Assert.That(SignalMath.SignFlips(input, output, threshold: 2000)).IsEqualTo(0);

        // And it must actually reach the rail rather than quietly attenuating.
        await Assert.That(output.Max()).IsGreaterThan((short)32000);
    }

    [Test]
    public async Task Silence_StaysSilent()
    {
        var output = SignalMath.Run(new GainEffect { Gain = 4f }, new short[2000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }

    [Test]
    public async Task ChangingGainMidStream_DoesNotClick()
    {
        var effect = new GainEffect { Gain = 1f };
        var input = SignalMath.Sine(200, 8000, amplitude: 8000);
        var output = input.ToArray();

        for (var offset = 0; offset < output.Length; offset += 320)
        {
            if (offset == 3200)
                effect.Gain = 0.1f;      // slam the fader mid-signal

            effect.Process(output.AsSpan(offset, 320), SignalMath.Rate);
        }

        // At 200 Hz / 8000 amplitude the waveform itself steps ~630 per sample; a click from an
        // unsmoothed gain jump would be an order of magnitude larger.
        await Assert.That(SignalMath.MaxStep(output)).IsLessThan(1200);
    }
}

public class NoiseGateEffectTests
{
    [Test]
    public async Task QuietSignal_IsSilenced()
    {
        var quiet = SignalMath.Sine(440, 8000, amplitude: 30);   // about -60 dBFS
        var output = SignalMath.Run(new NoiseGateEffect { ThresholdDb = -45f }, quiet);

        await Assert.That(SignalMath.Rms(output.AsSpan(2000))).IsLessThan(2.0);
    }

    [Test]
    public async Task LoudSignal_PassesThrough()
    {
        var loud = SignalMath.Sine(440, 8000, amplitude: 8000);
        var output = SignalMath.Run(new NoiseGateEffect { ThresholdDb = -45f }, loud);

        var ratio = SignalMath.Rms(output.AsSpan(3000)) / SignalMath.Rms(loud.AsSpan(3000));
        await Assert.That(ratio).IsEqualTo(1.0).Within(0.05);
    }

    [Test]
    public async Task Silence_StaysSilent()
    {
        var output = SignalMath.Run(new NoiseGateEffect(), new short[4000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }
}

public class BiquadFilterEffectTests
{
    [Test]
    public async Task LowPass_AttenuatesAboveCutoff()
    {
        var effect = new BiquadFilterEffect { Type = BiquadFilterType.LowPass, Frequency = 800f };
        effect.Reset();   // snap the smoothed cutoff so the first blocks are already at 800 Hz

        var low = SignalMath.Run(effect, SignalMath.Sine(300, 8000));
        effect.Reset();
        var high = SignalMath.Run(effect, SignalMath.Sine(5000, 8000));

        var lowKept = SignalMath.Energy(low.AsSpan(2000), 300);
        var highKept = SignalMath.Energy(high.AsSpan(2000), 5000);

        await Assert.That(highKept).IsLessThan(lowKept * 0.2);
    }

    [Test]
    public async Task HighPass_AttenuatesBelowCutoff()
    {
        var effect = new BiquadFilterEffect { Type = BiquadFilterType.HighPass, Frequency = 1000f };
        effect.Reset();

        var low = SignalMath.Run(effect, SignalMath.Sine(150, 8000));
        effect.Reset();
        var high = SignalMath.Run(effect, SignalMath.Sine(4000, 8000));

        await Assert.That(SignalMath.Energy(low.AsSpan(2000), 150))
            .IsLessThan(SignalMath.Energy(high.AsSpan(2000), 4000) * 0.2);
    }

    [Test]
    public async Task Notch_RemovesTheTargetToneAndKeepsTheRest()
    {
        var effect = new BiquadFilterEffect { Type = BiquadFilterType.Notch, Frequency = 1000f, Q = 8f };
        effect.Reset();

        var notched = SignalMath.Run(effect, SignalMath.Sine(1000, 8000));
        effect.Reset();
        var passed = SignalMath.Run(effect, SignalMath.Sine(3000, 8000));

        await Assert.That(SignalMath.Energy(notched.AsSpan(2000), 1000))
            .IsLessThan(SignalMath.Energy(passed.AsSpan(2000), 3000) * 0.2);
    }

    [Test]
    public async Task BandPass_KeepsTheCentreAndRejectsTheEdges()
    {
        var effect = new BiquadFilterEffect { Type = BiquadFilterType.BandPass, Frequency = 1000f, Q = 2f };
        effect.Reset();

        var centre = SignalMath.Run(effect, SignalMath.Sine(1000, 8000));
        effect.Reset();
        var far = SignalMath.Run(effect, SignalMath.Sine(6000, 8000));

        await Assert.That(SignalMath.Energy(far.AsSpan(2000), 6000))
            .IsLessThan(SignalMath.Energy(centre.AsSpan(2000), 1000) * 0.2);
    }

    [Test]
    public async Task NoiseThroughLowPass_LosesHighFrequencyEnergy()
    {
        var effect = new BiquadFilterEffect { Type = BiquadFilterType.LowPass, Frequency = 700f };
        effect.Reset();

        var input = SignalMath.Noise(16000);
        var output = SignalMath.Run(effect, input);

        var beforeHigh = SignalMath.Energy(input.AsSpan(2000), 5000);
        var afterHigh = SignalMath.Energy(output.AsSpan(2000), 5000);

        await Assert.That(afterHigh).IsLessThan(beforeHigh * 0.2);
    }

    [Test]
    public async Task Silence_StaysSilent()
    {
        var output = SignalMath.Run(new BiquadFilterEffect(), new short[4000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }

    [Test]
    public async Task FrequencyIsClampedBelowNyquist()
    {
        // Asking for a cutoff above Nyquist must not produce NaN coefficients.
        var effect = new BiquadFilterEffect { Type = BiquadFilterType.LowPass, Frequency = 50000f };
        var output = SignalMath.Run(effect, SignalMath.Sine(440, 4000));

        await Assert.That(output.All(s => s is > short.MinValue and < short.MaxValue)).IsTrue();
    }
}

public class DistortionEffectTests
{
    [Test]
    public async Task AddsHarmonics()
    {
        var input = SignalMath.Sine(500, 8000, amplitude: 9000);
        var output = SignalMath.Run(new DistortionEffect { Drive = 25f, Mix = 1f }, input);

        // A symmetric tanh curve generates odd harmonics: the third is the signature.
        var before = SignalMath.Energy(input.AsSpan(2000), 1500);
        var after = SignalMath.Energy(output.AsSpan(2000), 1500);

        await Assert.That(after).IsGreaterThan(before * 5);
    }

    [Test]
    public async Task SaturatesWithoutWrapping()
    {
        var input = SignalMath.Sine(300, 8000, amplitude: 30000);
        var output = SignalMath.Run(new DistortionEffect { Drive = 50f, Mix = 1f }, input);

        // tanh is sign-preserving, so any sign flip would mean the float→short cast wrapped.
        await Assert.That(SignalMath.SignFlips(input, output, threshold: 3000)).IsEqualTo(0);
    }

    [Test]
    public async Task Silence_StaysSilent()
    {
        var output = SignalMath.Run(new DistortionEffect(), new short[2000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }
}

public class RingModEffectTests
{
    [Test]
    public async Task ProducesSumAndDifferenceFrequencies()
    {
        // Ring modulating 1000 Hz by 100 Hz should move the energy to 900 and 1100 and gut the
        // original carrier — that redistribution is exactly what makes it sound robotic.
        var input = SignalMath.Sine(1000, 16000);
        var output = SignalMath.Run(new RingModEffect { Frequency = 100f, Mix = 1f }, input);

        var original = SignalMath.Energy(output.AsSpan(4000), 1000);
        var sideband = SignalMath.Energy(output.AsSpan(4000), 1100);

        await Assert.That(sideband).IsGreaterThan(original * 3);
    }

    [Test]
    public async Task Silence_StaysSilent()
    {
        var output = SignalMath.Run(new RingModEffect(), new short[2000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }
}

public class EchoEffectTests
{
    [Test]
    public async Task ImpulseReappearsAfterTheDelay()
    {
        const float delayMs = 100f;
        var effect = new EchoEffect { DelayMs = delayMs, Feedback = 0.5f, Mix = 1f };
        effect.Reset();

        var input = new short[8000];
        input[0] = 20000;

        var output = SignalMath.Run(effect, input);

        var expected = (int)(delayMs * 0.001f * SignalMath.Rate);   // 1600 samples
        var peak = 0;
        var peakIndex = 0;
        for (var i = 100; i < output.Length; i++)
        {
            if (Math.Abs(output[i]) > peak)
            {
                peak = Math.Abs(output[i]);
                peakIndex = i;
            }
        }

        await Assert.That(peak).IsGreaterThan(1000);
        await Assert.That(peakIndex).IsEqualTo(expected);
    }

    [Test]
    public async Task FeedbackProducesDecayingRepeats()
    {
        var effect = new EchoEffect { DelayMs = 50f, Feedback = 0.6f, Mix = 1f };
        effect.Reset();

        var input = new short[16000];
        input[0] = 20000;
        var output = SignalMath.Run(effect, input);

        var step = (int)(50f * 0.001f * SignalMath.Rate);
        var first = Math.Abs((int)output[step]);
        var second = Math.Abs((int)output[step * 2]);

        await Assert.That(second).IsLessThan(first);
        await Assert.That(second).IsGreaterThan(0);
    }

    [Test]
    public async Task Silence_StaysSilent()
    {
        var output = SignalMath.Run(new EchoEffect(), new short[4000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }

    [Test]
    public async Task FeedbackIsClampedBelowUnity()
    {
        var effect = new EchoEffect { Feedback = 5f };
        await Assert.That(effect.Feedback).IsEqualTo(0.95f);
    }
}

public class ChorusEffectTests
{
    [Test]
    public async Task ModulatesTheSignalWithoutBlowingUp()
    {
        var input = SignalMath.Sine(440, 16000);
        var output = SignalMath.Run(new ChorusEffect { RateHz = 2f, DepthMs = 8f, Mix = 0.5f }, input);

        await Assert.That(output.All(s => s is > short.MinValue and < short.MaxValue)).IsTrue();
        // The sweeping delay beats against the dry signal, so it cannot come out identical.
        await Assert.That(output.AsSpan(4000).SequenceEqual(input.AsSpan(4000))).IsFalse();
    }

    [Test]
    public async Task Silence_StaysSilent()
    {
        var output = SignalMath.Run(new ChorusEffect(), new short[4000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }
}

public class ReverbEffectTests
{
    [Test]
    public async Task ProducesATailAfterTheInputStops()
    {
        var effect = new ReverbEffect { RoomSize = 0.9f, Damping = 0.2f, Mix = 1f };
        effect.Reset();

        var input = new short[16000];
        for (var i = 0; i < 1600; i++)                     // 100 ms burst, then silence
            input[i] = (short)(12000 * MathF.Sin(2f * MathF.PI * 440 * i / SignalMath.Rate));

        var output = SignalMath.Run(effect, input);

        // Energy well after the burst can only come from the reverb tail.
        await Assert.That(SignalMath.Rms(output.AsSpan(4000, 4000))).IsGreaterThan(1.0);
    }

    [Test]
    public async Task TailDecays()
    {
        var effect = new ReverbEffect { RoomSize = 0.5f, Mix = 1f };
        effect.Reset();

        var input = new short[24000];
        for (var i = 0; i < 1600; i++)
            input[i] = (short)(12000 * MathF.Sin(2f * MathF.PI * 440 * i / SignalMath.Rate));

        var output = SignalMath.Run(effect, input);

        var early = SignalMath.Rms(output.AsSpan(3000, 3000));
        var late = SignalMath.Rms(output.AsSpan(18000, 3000));

        await Assert.That(late).IsLessThan(early);
    }

    [Test]
    public async Task LongestDecay_DoesNotRunAway()
    {
        // The comb feedback at RoomSize 1 is 0.98 — close to, but below, self-oscillation. If that
        // mapping ever crossed 1.0 the tail would grow instead of decaying. Measuring a burst's
        // tail is level-independent, unlike a clipping check.
        var effect = new ReverbEffect { RoomSize = 1f, Damping = 0f, Mix = 1f };
        effect.Reset();

        var input = new short[80000];   // 5 seconds
        for (var i = 0; i < 1600; i++)
            input[i] = (short)(12000 * MathF.Sin(2f * MathF.PI * 440 * i / SignalMath.Rate));

        var output = SignalMath.Run(effect, input);

        var early = SignalMath.Rms(output.AsSpan(8000, 8000));
        var late = SignalMath.Rms(output.AsSpan(64000, 8000));

        await Assert.That(early).IsGreaterThan(1.0);
        await Assert.That(late).IsLessThan(early);
    }

    [Test]
    public async Task NormalLevels_DoNotClip()
    {
        // A reverb whose wet gain is mis-scaled pegs everything; at sane input levels almost
        // nothing should reach the rail.
        var output = SignalMath.Run(
            new ReverbEffect { RoomSize = 0.7f, Mix = 0.5f },
            SignalMath.Sine(440, 32000, amplitude: 10000)
        );

        await Assert.That(SignalMath.RailedFraction(output)).IsLessThan(0.01);
    }

    [Test]
    public async Task Silence_StaysSilent()
    {
        var output = SignalMath.Run(new ReverbEffect(), new short[8000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }
}

public class PitchShiftEffectTests
{
    [Test]
    public async Task OctaveUp_DoublesTheFrequency()
    {
        var effect = new PitchShiftEffect { Semitones = 12f };
        effect.Reset();

        var input = SignalMath.Sine(440, 32000);
        var output = SignalMath.Run(effect, input);

        // Skip the first window, where the delay line is still filling.
        var steady = output.AsSpan(8000);
        var dominant = SignalMath.DominantOf(steady, 220, 440, 880, 1760);

        await Assert.That(dominant).IsEqualTo(880.0);
    }

    [Test]
    public async Task OctaveDown_HalvesTheFrequency()
    {
        var effect = new PitchShiftEffect { Semitones = -12f };
        effect.Reset();

        var output = SignalMath.Run(effect, SignalMath.Sine(880, 32000));
        var dominant = SignalMath.DominantOf(output.AsSpan(8000), 220, 440, 880, 1760);

        await Assert.That(dominant).IsEqualTo(440.0);
    }

    [Test]
    public async Task PerfectFifth_LandsOnTheRightPitch()
    {
        var effect = new PitchShiftEffect { Semitones = 7f };
        effect.Reset();

        var output = SignalMath.Run(effect, SignalMath.Sine(400, 32000));
        // 400 Hz × 2^(7/12) ≈ 599.4 Hz
        var dominant = SignalMath.DominantOf(output.AsSpan(8000), 400, 500, 599.4, 700, 800);

        await Assert.That(dominant).IsEqualTo(599.4);
    }

    [Test]
    public async Task LengthIsPreserved()
    {
        // The whole design depends on this: pitch changes, duration does not.
        var effect = new PitchShiftEffect { Semitones = 9f };
        var input = SignalMath.Sine(440, 12345);
        var output = SignalMath.Run(effect, input);

        await Assert.That(output.Length).IsEqualTo(input.Length);
    }

    [Test]
    public async Task ZeroSemitones_IsCleanPassthrough()
    {
        // With no shift there is no seam to hide, so the crossfade must not run — otherwise the
        // signal would comb-filter against itself and sound hollow at the "off" setting.
        var effect = new PitchShiftEffect { Semitones = 0f };
        var input = SignalMath.Sine(440, 8000);
        var output = SignalMath.Run(effect, input);

        await Assert.That(output.SequenceEqual(input)).IsTrue();
    }

    [Test]
    public async Task Ratio_MatchesSemitones()
    {
        await Assert.That(new PitchShiftEffect { Semitones = 12f }.Ratio).IsEqualTo(2f).Within(0.001f);
        await Assert.That(new PitchShiftEffect { Semitones = -12f }.Ratio).IsEqualTo(0.5f).Within(0.001f);
        await Assert.That(new PitchShiftEffect { Semitones = 0f }.Ratio).IsEqualTo(1f).Within(0.001f);
    }

    [Test]
    public async Task SemitonesAreClampedToTwoOctaves()
    {
        await Assert.That(new PitchShiftEffect { Semitones = 100f }.Semitones).IsEqualTo(24f);
        await Assert.That(new PitchShiftEffect { Semitones = -100f }.Semitones).IsEqualTo(-24f);
    }

    [Test]
    public async Task Silence_StaysSilent()
    {
        var output = SignalMath.Run(new PitchShiftEffect { Semitones = 5f }, new short[8000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }
}

public class AudioEffectPresetTests
{
    [Test]
    [MatrixDataSource]
    public async Task EveryPreset_BuildsAndProcessesSafely(
        [Matrix(
            AudioEffectPreset.Robot,
            AudioEffectPreset.Chipmunk,
            AudioEffectPreset.DeepVoice,
            AudioEffectPreset.Cathedral,
            AudioEffectPreset.Telephone,
            AudioEffectPreset.Megaphone,
            AudioEffectPreset.Ensemble)]
        AudioEffectPreset preset)
    {
        var chain = AudioEffectPresets.Create(preset);
        await Assert.That(chain.Count).IsGreaterThan(0);

        var output = SignalMath.Run(chain, SignalMath.Sine(440, 16000, amplitude: 12000));

        // A preset must produce usable audio: real energy (not NaN-collapsed to silence) and not
        // pinned at full scale (not blown up by stacked gain).
        await Assert.That(SignalMath.Rms(output.AsSpan(8000))).IsGreaterThan(1.0);
        await Assert.That(SignalMath.RailedFraction(output)).IsLessThan(0.25);
    }

    [Test]
    [MatrixDataSource]
    public async Task EveryPreset_LeavesSilenceAlone(
        [Matrix(
            AudioEffectPreset.Robot,
            AudioEffectPreset.Chipmunk,
            AudioEffectPreset.DeepVoice,
            AudioEffectPreset.Cathedral,
            AudioEffectPreset.Telephone,
            AudioEffectPreset.Megaphone,
            AudioEffectPreset.Ensemble)]
        AudioEffectPreset preset)
    {
        var output = SignalMath.Run(AudioEffectPresets.Create(preset), new short[8000]);
        await Assert.That(output.All(s => s == 0)).IsTrue();
    }
}
