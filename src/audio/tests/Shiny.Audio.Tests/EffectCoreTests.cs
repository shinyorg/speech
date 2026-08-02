using Shiny.Audio;

namespace Shiny.Audio.Tests;

/// <summary>Drives every sample to a fixed value — makes blending and bypass easy to observe.</summary>
file sealed class ConstantEffect(short value, bool enabled = true) : AudioEffect(enabled)
{
    public int ProcessCalls { get; private set; }
    public int ResetCalls { get; private set; }

    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        this.ProcessCalls++;
        samples.Fill(value);
    }

    public override void Reset() => this.ResetCalls++;
}

/// <summary>Adds a fixed offset — order-sensitive when combined, so chain ordering is testable.</summary>
file sealed class OffsetEffect(short offset) : AudioEffect
{
    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        for (var i = 0; i < samples.Length; i++)
            samples[i] = Clip(samples[i] + offset);
    }
}

/// <summary>Multiplies — combined with <see cref="OffsetEffect"/> this proves order matters.</summary>
file sealed class ScaleEffect(float factor) : AudioEffect
{
    protected override void ProcessCore(Span<short> samples, int sampleRate)
    {
        for (var i = 0; i < samples.Length; i++)
            samples[i] = Clip(samples[i] * factor);
    }
}

public class SmoothedParamTests
{
    [Test]
    public async Task Snap_TakesTargetImmediately()
    {
        var p = new SmoothedParam(0f) { Target = 5f };
        p.Snap();
        await Assert.That(p.Current).IsEqualTo(5f);
    }

    [Test]
    public async Task Ramp_ReachesTargetInSmoothingWindow()
    {
        var p = new SmoothedParam(0f) { Target = 1f };
        p.BeginBlock(1000, 16000);

        var expected = (int)(16000 * SmoothedParam.SmoothingMs / 1000f);   // 240 samples
        for (var i = 0; i < expected; i++)
            p.Next();

        await Assert.That(p.Current).IsEqualTo(1f).Within(0.0001f);
    }

    [Test]
    public async Task Ramp_IsMonotonicAndNeverOvershoots()
    {
        var p = new SmoothedParam(0f) { Target = 1f };
        p.BeginBlock(512, 16000);

        var previous = p.Current;
        for (var i = 0; i < 512; i++)
        {
            var value = p.Next();
            await Assert.That(value).IsGreaterThanOrEqualTo(previous);
            await Assert.That(value).IsLessThanOrEqualTo(1f);
            previous = value;
        }
    }

    [Test]
    public async Task Ramp_HandlesDownwardMoves()
    {
        var p = new SmoothedParam(1f) { Target = 0f };
        p.BeginBlock(512, 16000);
        for (var i = 0; i < 512; i++)
            p.Next();

        await Assert.That(p.Current).IsEqualTo(0f).Within(0.0001f);
    }

    [Test]
    public async Task Ramp_IsCappedBySmoothingWindowNotBlockSize()
    {
        // A very long buffer must not stretch the ramp across the whole thing — otherwise the
        // responsiveness of a slider would depend on the platform's buffer size.
        var p = new SmoothedParam(0f) { Target = 1f };
        p.BeginBlock(16000, 16000);

        for (var i = 0; i < 240; i++)
            p.Next();

        await Assert.That(p.Current).IsEqualTo(1f).Within(0.0001f);
    }

    [Test]
    public async Task SteadyState_NeedsNoInterpolation()
    {
        var p = new SmoothedParam(0.5f);
        p.BeginBlock(256, 16000);
        await Assert.That(p.IsSteady).IsTrue();
        await Assert.That(p.Next()).IsEqualTo(0.5f);
    }
}

public class AudioEffectTests
{
    static short[] Ramp(int n, short value = -8000)
    {
        var s = new short[n];
        Array.Fill(s, value);
        return s;
    }

    [Test]
    public async Task DisabledFromConstruction_IsPurePassthrough()
    {
        var effect = new ConstantEffect(9000, enabled: false);
        var samples = Ramp(256);
        var original = samples.ToArray();

        effect.Process(samples, 16000);

        await Assert.That(samples.SequenceEqual(original)).IsTrue();
        await Assert.That(effect.ProcessCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Enabled_AppliesEffect()
    {
        var effect = new ConstantEffect(9000);
        var samples = Ramp(256);

        effect.Process(samples, 16000);

        await Assert.That(samples.All(s => s == 9000)).IsTrue();
    }

    [Test]
    public async Task Disabling_CrossfadesRatherThanJumping()
    {
        var effect = new ConstantEffect(8000);
        // Settle fully wet first.
        effect.Process(Ramp(1024), 16000);

        effect.Enabled = false;
        var samples = Ramp(1024);
        effect.Process(samples, 16000);

        // It must travel from wet (+8000) toward dry (-8000) gradually, not in one step.
        await Assert.That(samples[0]).IsGreaterThan((short)7000);
        await Assert.That(samples[^1]).IsEqualTo((short)-8000);

        var maxJump = 0;
        for (var i = 1; i < samples.Length; i++)
            maxJump = Math.Max(maxJump, Math.Abs(samples[i] - samples[i - 1]));

        // A hard switch would be a 16000-unit step; a 12 ms fade is under 100 per sample.
        await Assert.That(maxJump).IsLessThan(150);
    }

    [Test]
    public async Task Enabling_CrossfadesRatherThanJumping()
    {
        var effect = new ConstantEffect(8000, enabled: false);
        effect.Process(Ramp(256), 16000);

        effect.Enabled = true;
        var samples = Ramp(1024);
        effect.Process(samples, 16000);

        await Assert.That(samples[0]).IsLessThan((short)-7000);
        await Assert.That(samples[^1]).IsEqualTo((short)8000);

        var maxJump = 0;
        for (var i = 1; i < samples.Length; i++)
            maxJump = Math.Max(maxJump, Math.Abs(samples[i] - samples[i - 1]));

        await Assert.That(maxJump).IsLessThan(150);
    }

    [Test]
    public async Task FadingOutCompletely_ResetsInternalState()
    {
        var effect = new ConstantEffect(8000);
        effect.Process(Ramp(1024), 16000);

        effect.Enabled = false;
        effect.Process(Ramp(1024), 16000);   // long enough to complete the fade

        // Reset on full bypass means re-enabling does not replay a stale delay line.
        await Assert.That(effect.ResetCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task SettledBypass_SkipsProcessingEntirely()
    {
        var effect = new ConstantEffect(8000);
        effect.Process(Ramp(1024), 16000);
        effect.Enabled = false;
        effect.Process(Ramp(1024), 16000);   // completes the fade

        var before = effect.ProcessCalls;
        effect.Process(Ramp(1024), 16000);

        await Assert.That(effect.ProcessCalls).IsEqualTo(before);
    }

    [Test]
    public async Task EmptyBuffer_IsHandled()
    {
        var effect = new ConstantEffect(8000);
        effect.Process(Span<short>.Empty, 16000);
        await Assert.That(effect.ProcessCalls).IsEqualTo(0);
    }
}

public class AudioEffectChainTests
{
    [Test]
    public async Task EmptyChain_IsPassthrough()
    {
        var chain = new AudioEffectChain();
        var samples = new short[] { 1, 2, 3, 4 };
        var original = samples.ToArray();

        chain.Process(samples, 16000);

        await Assert.That(samples.SequenceEqual(original)).IsTrue();
    }

    [Test]
    public async Task Add_ReturnsTheInstanceForLiveControl()
    {
        var chain = new AudioEffectChain();
        var effect = new OffsetEffect(100);

        var returned = chain.Add(effect);

        await Assert.That(ReferenceEquals(returned, effect)).IsTrue();
        await Assert.That(chain.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Effects_AreAppliedInOrder()
    {
        // (0 + 100) * 2 = 200, whereas the reverse order would give (0 * 2) + 100 = 100.
        var chain = new AudioEffectChain();
        chain.Add(new OffsetEffect(100));
        chain.Add(new ScaleEffect(2f));

        var samples = new short[8];
        chain.Process(samples, 16000);

        await Assert.That(samples.All(s => s == 200)).IsTrue();
    }

    [Test]
    public async Task Remove_TakesTheEffectOutOfTheSignal()
    {
        var chain = new AudioEffectChain();
        var offset = chain.Add(new OffsetEffect(100));
        chain.Add(new ScaleEffect(2f));

        await Assert.That(chain.Remove(offset)).IsTrue();
        await Assert.That(chain.Remove(offset)).IsFalse();

        var samples = new short[8];
        chain.Process(samples, 16000);
        await Assert.That(samples.All(s => s == 0)).IsTrue();
    }

    [Test]
    public async Task Clear_EmptiesTheChain()
    {
        var chain = new AudioEffectChain();
        chain.Add(new OffsetEffect(100));
        chain.Clear();

        await Assert.That(chain.Count).IsEqualTo(0);

        var samples = new short[4];
        chain.Process(samples, 16000);
        await Assert.That(samples.All(s => s == 0)).IsTrue();
    }

    [Test]
    public async Task MasterBypass_CrossfadesAndThenPassesThrough()
    {
        var chain = new AudioEffectChain();
        chain.Add(new ConstantEffect(8000));

        chain.Process(new short[1024], 16000);          // settle wet
        chain.Enabled = false;
        chain.Process(new short[1024], 16000);          // complete the fade

        var samples = new short[64];
        Array.Fill(samples, (short)-4000);
        chain.Process(samples, 16000);

        await Assert.That(samples.All(s => s == -4000)).IsTrue();
    }

    [Test]
    public async Task PerEffectToggle_IsIndependentOfTheChain()
    {
        var chain = new AudioEffectChain();
        var offset = chain.Add(new OffsetEffect(100));
        chain.Add(new OffsetEffect(20));

        offset.Enabled = false;
        chain.Process(new short[2048], 16000);          // let the fade complete

        var samples = new short[64];
        chain.Process(samples, 16000);

        await Assert.That(samples.All(s => s == 20)).IsTrue();
    }

    [Test]
    public async Task ByteOverload_ProcessesPcm16InPlace()
    {
        var chain = new AudioEffectChain();
        chain.Add(new OffsetEffect(1));

        var pcm = new byte[8];                          // four 16-bit zero samples
        chain.Process(pcm.AsSpan(), 16000);

        await Assert.That(pcm[0]).IsEqualTo((byte)1);
        await Assert.That(pcm[1]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Reset_CascadesToChildren()
    {
        var chain = new AudioEffectChain();
        var effect = chain.Add(new ConstantEffect(1000));

        chain.Reset();

        await Assert.That(effect.ResetCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentMutation_DoesNotDisruptProcessing()
    {
        // The audio thread snapshots the array per buffer, so mutation from another thread can
        // never expose a half-built chain.
        var chain = new AudioEffectChain();
        chain.Add(new OffsetEffect(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var mutator = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var e = chain.Add(new OffsetEffect(1));
                chain.Remove(e);
            }
        });

        var buffers = 0;
        while (!cts.IsCancellationRequested)
        {
            chain.Process(new short[256], 16000);
            buffers++;
        }
        await mutator;

        await Assert.That(buffers).IsGreaterThan(0);
        await Assert.That(chain.Count).IsGreaterThanOrEqualTo(1);
    }
}
