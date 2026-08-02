namespace Shiny.Audio.Tests;

/// <summary>Signal generation and measurement helpers used to verify the effects.</summary>
static class SignalMath
{
    public const int Rate = 16000;

    /// <summary>Generate a sine wave at PCM16 scale.</summary>
    public static short[] Sine(float frequency, int samples, float amplitude = 8000f, int sampleRate = Rate)
    {
        var buffer = new short[samples];
        for (var i = 0; i < samples; i++)
            buffer[i] = (short)(amplitude * MathF.Sin(2f * MathF.PI * frequency * i / sampleRate));

        return buffer;
    }

    /// <summary>Deterministic pseudo-random noise — no Random, so failures reproduce exactly.</summary>
    public static short[] Noise(int samples, float amplitude = 6000f, uint seed = 12345)
    {
        var buffer = new short[samples];
        var state = seed;
        for (var i = 0; i < samples; i++)
        {
            state = state * 1664525u + 1013904223u;                  // numerical recipes LCG
            var unit = (state >> 8) / (float)(1 << 24) * 2f - 1f;    // -1 .. 1
            buffer[i] = (short)(unit * amplitude);
        }
        return buffer;
    }

    /// <summary>
    /// Energy at a single frequency via the Goertzel algorithm — a one-bin DFT. Cheaper and clearer
    /// than pulling in an FFT just to ask "how much 880 Hz is in here?".
    /// </summary>
    public static double Energy(ReadOnlySpan<short> samples, double frequency, int sampleRate = Rate)
    {
        var omega = 2.0 * Math.PI * frequency / sampleRate;
        var coefficient = 2.0 * Math.Cos(omega);

        double s1 = 0, s2 = 0;
        foreach (var sample in samples)
        {
            var s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return Math.Sqrt(s1 * s1 + s2 * s2 - coefficient * s1 * s2) / samples.Length;
    }

    /// <summary>Find which of the candidate frequencies carries the most energy.</summary>
    public static double DominantOf(ReadOnlySpan<short> samples, params double[] candidates)
    {
        var best = candidates[0];
        var bestEnergy = Energy(samples, best);

        foreach (var candidate in candidates)
        {
            var energy = Energy(samples, candidate);
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                best = candidate;
            }
        }
        return best;
    }

    public static double Rms(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (var sample in samples)
            sum += (double)sample * sample;

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>
    /// Count samples where the output sign disagrees with the input's, ignoring near-zero input.
    /// A sign-preserving effect that wraps instead of saturating shows up here immediately —
    /// something a range check cannot detect, since a wrapped value is still a valid short.
    /// </summary>
    public static int SignFlips(ReadOnlySpan<short> input, ReadOnlySpan<short> output, int threshold)
    {
        var flips = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (Math.Abs((int)input[i]) < threshold)
                continue;

            if (Math.Sign(input[i]) != Math.Sign(output[i]) && output[i] != 0)
                flips++;
        }
        return flips;
    }

    /// <summary>
    /// Fraction of samples pinned at full scale. Occasional saturation is normal; a runaway
    /// feedback loop pegs nearly everything.
    /// </summary>
    public static double RailedFraction(ReadOnlySpan<short> samples)
    {
        var railed = 0;
        foreach (var sample in samples)
        {
            if (sample is short.MaxValue or short.MinValue)
                railed++;
        }
        return (double)railed / samples.Length;
    }

    /// <summary>Largest sample-to-sample step — a proxy for audible clicks.</summary>
    public static int MaxStep(ReadOnlySpan<short> samples)
    {
        var max = 0;
        for (var i = 1; i < samples.Length; i++)
            max = Math.Max(max, Math.Abs(samples[i] - samples[i - 1]));

        return max;
    }

    /// <summary>Run an effect over a signal in realistic buffer-sized chunks.</summary>
    public static short[] Run(IAudioEffect effect, short[] input, int blockSize = 320, int sampleRate = Rate)
    {
        var output = input.ToArray();
        for (var offset = 0; offset < output.Length; offset += blockSize)
        {
            var count = Math.Min(blockSize, output.Length - offset);
            effect.Process(output.AsSpan(offset, count), sampleRate);
        }
        return output;
    }
}
