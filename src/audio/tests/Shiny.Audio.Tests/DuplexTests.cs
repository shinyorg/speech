using System.Buffers.Binary;
using Shiny.Audio.Duplex;

namespace Shiny.Audio.Tests;

/// <summary>
/// The platform-agnostic half of <see cref="IAudioDuplex"/>: the playback jitter buffer, capture frame
/// chunking, option validation, the session plumbing (bounded capture channel, once-only stop) and the
/// one-session-at-a-time slot. The radios and echo cancellers need real devices.
/// </summary>
public class DuplexTests
{
    const int Rate = 16000;
    const int BytesPerMs = Rate / 1000 * 2;   // 32

    static AudioJitterBuffer Jitter(int targetMs = 60, int maxMs = 200)
        => new(Rate, TimeSpan.FromMilliseconds(targetMs), TimeSpan.FromMilliseconds(maxMs));

    // PCM16 whose samples count up from `start`, so order and loss are visible after a round trip.
    static byte[] Ramp(int ms, short start = 1)
    {
        var data = new byte[ms * BytesPerMs];
        for (var i = 0; i < data.Length / 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)(start + i));
        return data;
    }

    static short Sample(ReadOnlySpan<byte> pcm, int index)
        => BinaryPrimitives.ReadInt16LittleEndian(pcm[(index * 2)..]);

    static bool IsSilent(ReadOnlySpan<byte> pcm) => pcm.IndexOfAnyExcept((byte)0) < 0;


    // ---- jitter buffer ---------------------------------------------------------------------------

    [Test]
    public async Task Jitter_PlaysSilenceUntilPrimed()
    {
        var jitter = Jitter(targetMs: 60);
        var frame = new byte[20 * BytesPerMs];

        jitter.Write(Ramp(40));
        var read = jitter.Read(frame);

        await Assert.That(read).IsEqualTo(0);
        await Assert.That(IsSilent(frame)).IsTrue();
        await Assert.That(jitter.IsPlaying).IsFalse();
        await Assert.That(jitter.Buffered).IsEqualTo(TimeSpan.FromMilliseconds(40));   // nothing consumed
    }

    [Test]
    public async Task Jitter_StartsOnceTargetIsQueued_InOrder()
    {
        var jitter = Jitter(targetMs: 60);
        jitter.Write(Ramp(20, start: 1));
        jitter.Write(Ramp(20, start: 321));
        jitter.Write(Ramp(20, start: 641));

        var frame = new byte[20 * BytesPerMs];
        var read = jitter.Read(frame);

        await Assert.That(read).IsEqualTo(frame.Length);
        await Assert.That(jitter.IsPlaying).IsTrue();
        await Assert.That(Sample(frame, 0)).IsEqualTo((short)1);
        await Assert.That(Sample(frame, 319)).IsEqualTo((short)320);

        jitter.Read(frame);
        await Assert.That(Sample(frame, 0)).IsEqualTo((short)321);
        await Assert.That(jitter.Buffered).IsEqualTo(TimeSpan.FromMilliseconds(20));
    }

    [Test]
    public async Task Jitter_Underrun_FillsSilenceAndReprimes()
    {
        var jitter = Jitter(targetMs: 60);
        jitter.Write(Ramp(70));

        var frame = new byte[20 * BytesPerMs];
        jitter.Read(frame);
        jitter.Read(frame);
        jitter.Read(frame);           // 10 ms left

        var read = jitter.Read(frame);  // ran dry halfway
        await Assert.That(read).IsEqualTo(10 * BytesPerMs);
        await Assert.That(IsSilent(frame.AsSpan(read))).IsTrue();
        await Assert.That(IsSilent(frame.AsSpan(0, read))).IsFalse();
        await Assert.That(jitter.Underruns).IsEqualTo(1);
        await Assert.That(jitter.IsPlaying).IsFalse();

        // Re-priming: 40 ms isn't enough to start again...
        jitter.Write(Ramp(40));
        await Assert.That(jitter.Read(frame)).IsEqualTo(0);

        // ...60 ms is.
        jitter.Write(Ramp(20));
        await Assert.That(jitter.Read(frame)).IsEqualTo(frame.Length);
    }

    [Test]
    public async Task Jitter_EmptyWhilePlaying_IsAnUnderrun()
    {
        var jitter = Jitter(targetMs: 20);
        jitter.Write(Ramp(20));

        var frame = new byte[20 * BytesPerMs];
        await Assert.That(jitter.Read(frame)).IsEqualTo(frame.Length);
        await Assert.That(jitter.Read(frame)).IsEqualTo(0);
        await Assert.That(IsSilent(frame)).IsTrue();
        await Assert.That(jitter.Underruns).IsEqualTo(1);
    }

    [Test]
    public async Task Jitter_Overflow_DropsOldestBackToTarget()
    {
        var jitter = Jitter(targetMs: 60, maxMs: 200);
        for (var i = 0; i < 10; i++)
            jitter.Write(Ramp(20, start: (short)(i * 320 + 1)));   // 200 ms: at the limit, nothing dropped

        await Assert.That(jitter.Buffered).IsEqualTo(TimeSpan.FromMilliseconds(200));
        await Assert.That(jitter.DroppedBytes).IsEqualTo(0L);

        jitter.Write(Ramp(20, start: 3201));   // 220 ms: over, back to the newest 60 ms

        await Assert.That(jitter.Buffered).IsEqualTo(TimeSpan.FromMilliseconds(60));
        await Assert.That(jitter.DroppedBytes).IsEqualTo(160L * BytesPerMs);

        var frame = new byte[20 * BytesPerMs];
        jitter.Read(frame);
        await Assert.That(Sample(frame, 0)).IsEqualTo((short)(8 * 320 + 1));   // the 9th packet is now the oldest
    }

    [Test]
    public async Task Jitter_WriteLargerThanMax_KeepsItsNewestPart()
    {
        var jitter = Jitter(targetMs: 60, maxMs: 200);
        jitter.Write(Ramp(20));
        jitter.Write(Ramp(300, start: 1));

        await Assert.That(jitter.Buffered).IsEqualTo(TimeSpan.FromMilliseconds(200));

        var frame = new byte[20 * BytesPerMs];
        jitter.Read(frame);
        await Assert.That(Sample(frame, 0)).IsEqualTo((short)(100 * 16 + 1));   // the last 200 ms of the 300 ms write
    }

    [Test]
    public async Task Jitter_ZeroTarget_PlaysImmediately_AndOverflowKeepsLatestWrite()
    {
        var jitter = Jitter(targetMs: 0, maxMs: 40);
        jitter.Write(Ramp(20));
        jitter.Write(Ramp(20));
        jitter.Write(Ramp(20, start: 1000));   // over 40 ms: keep at least this write

        await Assert.That(jitter.Buffered).IsEqualTo(TimeSpan.FromMilliseconds(20));

        var frame = new byte[20 * BytesPerMs];
        await Assert.That(jitter.Read(frame)).IsEqualTo(frame.Length);
        await Assert.That(Sample(frame, 0)).IsEqualTo((short)1000);
    }

    [Test]
    public async Task Jitter_OddTrailingByte_IsIgnored()
    {
        var jitter = Jitter(targetMs: 0);
        jitter.Write(new byte[] { 1, 0, 2, 0, 9 });

        await Assert.That(jitter.BufferedBytes).IsEqualTo(4);

        var frame = new byte[4];
        jitter.Read(frame);
        await Assert.That(Sample(frame, 0)).IsEqualTo((short)1);
        await Assert.That(Sample(frame, 1)).IsEqualTo((short)2);
    }

    [Test]
    public async Task Jitter_WrapsAroundTheRingWithoutCorruption()
    {
        // Odd-sized writes and reads walk the head and tail all the way round the ring many times.
        var jitter = Jitter(targetMs: 10, maxMs: 30);
        short next = 1, expected = 1;
        var read = new byte[7 * 2];
        var write = new byte[11 * 2];

        for (var round = 0; round < 2000; round++)
        {
            for (var i = 0; i < 11; i++)
                BinaryPrimitives.WriteInt16LittleEndian(write.AsSpan(i * 2), next++);
            jitter.Write(write);

            while (jitter.BufferedBytes >= read.Length)
            {
                var n = jitter.Read(read);
                for (var i = 0; i < n / 2; i++)
                {
                    var s = Sample(read, i);
                    if (s != expected)
                        throw new Exception($"Round {round}: expected sample {expected}, got {s}");
                    expected++;
                }
                if (n == 0)
                    break;   // priming
            }
        }

        await Assert.That(jitter.DroppedBytes).IsEqualTo(0L);
        await Assert.That((int)expected).IsGreaterThan(20000);
    }

    [Test]
    public async Task Jitter_Clear_EmptiesAndReprimes()
    {
        var jitter = Jitter(targetMs: 20);
        jitter.Write(Ramp(60));
        jitter.Read(new byte[20 * BytesPerMs]);

        jitter.Clear();

        await Assert.That(jitter.Buffered).IsEqualTo(TimeSpan.Zero);
        await Assert.That(jitter.IsPlaying).IsFalse();
    }

    [Test]
    public async Task Jitter_ConcurrentWriterAndReader_KeepOrder()
    {
        // A generous max so nothing is dropped: then every sample read must be the next one written.
        var jitter = new AudioJitterBuffer(Rate, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        const int total = 200_000;
        var errors = 0;

        var writer = Task.Run(() =>
        {
            var packet = new byte[160 * 2];
            for (var s = 0; s < total;)
            {
                var n = Math.Min(160, total - s);
                for (var i = 0; i < n; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(i * 2), (short)(s++ % 30000));
                jitter.Write(packet.AsSpan(0, n * 2));
            }
        });

        var reader = Task.Run(() =>
        {
            var frame = new byte[320 * 2];
            var seen = 0;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (seen < total && DateTime.UtcNow < deadline)
            {
                var n = jitter.Read(frame) / 2;
                for (var i = 0; i < n; i++, seen++)
                {
                    if (Sample(frame, i) != (short)(seen % 30000))
                        Interlocked.Increment(ref errors);
                }
            }
            return seen;
        });

        await writer;
        var seen = await reader;

        await Assert.That(seen).IsEqualTo(total);
        await Assert.That(errors).IsEqualTo(0);
    }


    // ---- frame chunker ---------------------------------------------------------------------------

    [Test]
    public async Task Chunker_EmitsExactFrames_AndCarriesTheRemainder()
    {
        var chunker = new PcmFrameChunker(640);
        var frames = new List<byte[]>();

        chunker.Push(Ramp(15, start: 1), frames.Add);    // 480 bytes: nothing yet
        await Assert.That(frames.Count).IsEqualTo(0);
        await Assert.That(chunker.Pending).IsEqualTo(480);

        chunker.Push(Ramp(50, start: 241), frames.Add);  // +1600 = 2080: three frames, 160 left
        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(frames.All(f => f.Length == 640)).IsTrue();
        await Assert.That(chunker.Pending).IsEqualTo(160);

        // Contiguous across the push boundary: samples 1..960 in order.
        var all = frames.SelectMany(f => f).ToArray();
        for (var i = 0; i < all.Length / 2; i++)
            if (Sample(all, i) != i + 1)
                throw new Exception($"Sample {i} out of order");
    }

    [Test]
    public async Task Chunker_FramesAreIndependentCopies()
    {
        var chunker = new PcmFrameChunker(4);
        var frames = new List<byte[]>();

        chunker.Push(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frames.Add);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0]).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
        await Assert.That(frames[1]).IsEquivalentTo(new byte[] { 5, 6, 7, 8 });
        await Assert.That(ReferenceEquals(frames[0], frames[1])).IsFalse();
    }

    [Test]
    public async Task Chunker_Reset_DropsThePartialFrame()
    {
        var chunker = new PcmFrameChunker(4);
        var frames = new List<byte[]>();
        chunker.Push(new byte[] { 1, 2 }, frames.Add);
        chunker.Reset();
        chunker.Push(new byte[] { 5, 6, 7, 8 }, frames.Add);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(frames[0]).IsEquivalentTo(new byte[] { 5, 6, 7, 8 });
    }


    // ---- options ---------------------------------------------------------------------------------

    [Test]
    public async Task Options_Defaults()
    {
        var o = new AudioDuplexOptions();
        await Assert.That(o.SampleRate).IsEqualTo(16000);
        await Assert.That(o.Speakerphone).IsTrue();
        await Assert.That(o.Processing).IsEqualTo(AudioProcessingOptions.VoiceChat);
        await Assert.That(o.FrameDuration).IsEqualTo(TimeSpan.FromMilliseconds(20));
        await Assert.That(o.PlaybackLatency).IsEqualTo(TimeSpan.FromMilliseconds(60));
        await Assert.That(o.MaxPlaybackLatency).IsEqualTo(TimeSpan.FromMilliseconds(200));
        await Assert.That(o.InputDevice).IsNull();
        await Assert.That(o.FrameBytes).IsEqualTo(640);
        await Assert.That((o with { SampleRate = 8000 }).FrameBytes).IsEqualTo(320);
    }

    [Test]
    [Arguments(44100, 20, 60, 200)]
    [Arguments(16000, 5, 60, 200)]
    [Arguments(16000, 150, 60, 200)]
    [Arguments(16000, 20, -1, 200)]
    [Arguments(16000, 20, 200, 200)]
    public async Task Options_InvalidAreRejected(int rate, int frameMs, int targetMs, int maxMs)
    {
        var o = new AudioDuplexOptions
        {
            SampleRate = rate,
            FrameDuration = TimeSpan.FromMilliseconds(frameMs),
            PlaybackLatency = TimeSpan.FromMilliseconds(targetMs),
            MaxPlaybackLatency = TimeSpan.FromMilliseconds(maxMs)
        };
        await Assert.That(o.Validate).Throws<ArgumentException>();
    }

    [Test]
    public async Task Options_FractionalFrameDuration_IsRejected()
    {
        var o = new AudioDuplexOptions { FrameDuration = TimeSpan.FromMilliseconds(20.5) };
        await Assert.That(o.Validate).Throws<ArgumentException>();
    }


    static AudioDevice Mic(string id, AudioDeviceType type = AudioDeviceType.BuiltInMic, bool current = false)
        => new(id, $"Mic {id}", AudioDeviceIo.Input, type, current);

    static AudioDevice Speaker(string id)
        => new(id, $"Speaker {id}", AudioDeviceIo.Output, AudioDeviceType.BuiltInSpeaker, false);

    [Test]
    public async Task Options_InputDevice_MustBeAnInput()
    {
        new AudioDuplexOptions { InputDevice = Mic("headset", AudioDeviceType.Bluetooth) }.Validate();

        var o = new AudioDuplexOptions { InputDevice = Speaker("spk") };
        await Assert.That(o.Validate).Throws<ArgumentException>();
    }

    [Test]
    public async Task Slot_OutputAsInputDevice_IsRejectedBeforeStarting()
    {
        var slot = new AudioDuplexSlot();
        var started = false;

        await Assert.That(() => (Task)slot.StartAsync(new AudioDuplexOptions { InputDevice = Speaker("spk") }, default, (o, _) =>
        {
            started = true;
            return Task.FromResult<AudioDuplexSession>(new FakeSession(o));
        })).Throws<ArgumentException>();

        await Assert.That(started).IsFalse();
    }


    // ---- session plumbing ------------------------------------------------------------------------

    sealed class FakeSession(AudioDuplexOptions options, Func<Task>? onStop = null) : AudioDuplexSession(options)
    {
        public int StopCalls;
        public override bool Speakerphone { get; set; } = options.Speakerphone;
        public void Capture(ReadOnlySpan<byte> pcm) => this.OnCaptured(pcm);
        public int Pull(Span<byte> destination) => this.ReadPlayback(destination);

        // The platform hook: records each switch, and reports a device as unavailable when it's listed.
        public readonly List<AudioDevice?> Selected = [];
        public readonly HashSet<string> Unavailable = [];

        protected override bool SelectInputDevice(AudioDevice? device)
        {
            this.Selected.Add(device);
            return device == null || !this.Unavailable.Contains(device.Id);
        }

        public void LoseInput(bool notify = true) => this.OnInputDeviceUnavailable(notify);

        protected override Task StopCoreAsync()
        {
            Interlocked.Increment(ref this.StopCalls);
            return onStop?.Invoke() ?? Task.CompletedTask;
        }
    }

    [Test]
    public async Task Session_CapturedIsFramed()
    {
        await using var session = new FakeSession(new AudioDuplexOptions());

        session.Capture(Ramp(30));   // a 20 ms frame and 10 ms held back
        session.Capture(Ramp(10));   // completes the second

        await Assert.That(session.Captured.TryRead(out var first)).IsTrue();
        await Assert.That(session.Captured.TryRead(out var second)).IsTrue();
        await Assert.That(session.Captured.TryRead(out _)).IsFalse();
        await Assert.That(first.Length).IsEqualTo(640);
        await Assert.That(second.Length).IsEqualTo(640);
        await Assert.That(session.SampleRate).IsEqualTo(16000);
    }

    [Test]
    public async Task Session_SlowReader_LosesTheOldestFrames()
    {
        await using var session = new FakeSession(new AudioDuplexOptions());
        var capacity = (int)(AudioDuplexSession.CapturedCapacity.TotalMilliseconds / 20);

        for (var i = 0; i < capacity + 10; i++)
            session.Capture(Ramp(20, start: (short)i));

        var items = new List<ReadOnlyMemory<byte>>();
        while (session.Captured.TryRead(out var f))
            items.Add(f);

        await Assert.That(items.Count).IsEqualTo(capacity);
        await Assert.That(Sample(items[0].Span, 0)).IsEqualTo((short)10);   // frames 0-9 were dropped
    }

    [Test]
    public async Task Session_PlayFeedsTheJitterBuffer()
    {
        await using var session = new FakeSession(new AudioDuplexOptions { PlaybackLatency = TimeSpan.FromMilliseconds(40) });

        session.Play(Ramp(20));
        session.Play(Ramp(20));
        await Assert.That(session.Buffered).IsEqualTo(TimeSpan.FromMilliseconds(40));

        var frame = new byte[640];
        await Assert.That(session.Pull(frame)).IsEqualTo(640);
        await Assert.That(session.Buffered).IsEqualTo(TimeSpan.FromMilliseconds(20));
    }

    [Test]
    public async Task Session_RaisesInputLevel()
    {
        await using var session = new FakeSession(new AudioDuplexOptions());
        var levels = new List<double>();
        session.InputLevelChanged += (_, l) => levels.Add(l);

        var loud = new byte[640];
        for (var i = 0; i < 320; i++)
            BinaryPrimitives.WriteInt16LittleEndian(loud.AsSpan(i * 2), (short)(i % 2 == 0 ? 16000 : -16000));
        session.Capture(loud);

        await Assert.That(levels.Count).IsEqualTo(1);
        await Assert.That(levels[0]).IsGreaterThan(0.5);
    }

    [Test]
    public async Task Session_Stop_CompletesCaptured_OnceOnly_AndIgnoresLatePlay()
    {
        var session = new FakeSession(new AudioDuplexOptions());
        session.Play(Ramp(100));
        session.Capture(Ramp(20));

        var first = session.StopAsync();
        var second = session.StopAsync();
        await first;
        await second;
        await session.DisposeAsync();

        await Assert.That(session.StopCalls).IsEqualTo(1);
        await Assert.That(session.Buffered).IsEqualTo(TimeSpan.Zero);

        // Frames captured before the stop are still readable; then the channel completes.
        var drained = 0;
        await foreach (var _ in session.Captured.ReadAllAsync())
            drained++;
        await Assert.That(drained).IsEqualTo(1);
        await Assert.That(session.Captured.Completion.IsCompleted).IsTrue();

        session.Play(Ramp(20));
        session.Capture(Ramp(20));
        await Assert.That(session.Buffered).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Session_InputDevice_StartsFromOptions()
    {
        var mic = Mic("usb", AudioDeviceType.Usb);
        await using var session = new FakeSession(new AudioDuplexOptions { InputDevice = mic });

        await Assert.That(session.InputDevice).IsEqualTo(mic);
        await Assert.That(session.Selected.Count).IsEqualTo(0);   // the platform applies the option itself at start
    }

    [Test]
    public async Task Session_InputDevice_Setter_ForwardsToThePlatform()
    {
        await using var session = new FakeSession(new AudioDuplexOptions());
        var headset = Mic("bt", AudioDeviceType.Bluetooth);

        session.InputDevice = headset;
        await Assert.That(session.InputDevice).IsEqualTo(headset);

        session.InputDevice = null;
        await Assert.That(session.InputDevice).IsNull();

        await Assert.That(session.Selected).IsEquivalentTo(new AudioDevice?[] { headset, null });
    }

    [Test]
    public async Task Session_InputDevice_SameIdIsANoOp()
    {
        await using var session = new FakeSession(new AudioDuplexOptions());

        session.InputDevice = Mic("a");
        session.InputDevice = Mic("a", current: true);   // a fresh enumeration of the same mic
        session.InputDevice = null;
        session.InputDevice = null;

        await Assert.That(session.Selected.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Session_InputDevice_Unavailable_FallsBackToDefault()
    {
        await using var session = new FakeSession(new AudioDuplexOptions());
        var raised = 0;
        session.InputDeviceChanged += (_, _) => raised++;
        session.Unavailable.Add("gone");

        session.InputDevice = Mic("gone");

        await Assert.That(session.Selected.Count).IsEqualTo(1);
        await Assert.That(session.InputDevice).IsNull();
        await Assert.That(raised).IsEqualTo(0);   // a response to your own set, not a platform change
    }

    [Test]
    public async Task Session_InputDevice_OutputIsRejected()
    {
        await using var session = new FakeSession(new AudioDuplexOptions());

        await Assert.That(() => session.InputDevice = Speaker("spk")).Throws<ArgumentException>();
        await Assert.That(session.Selected.Count).IsEqualTo(0);
        await Assert.That(session.InputDevice).IsNull();
    }

    [Test]
    public async Task Session_InputDeviceLost_ResetsAndNotifiesOnce()
    {
        await using var session = new FakeSession(new AudioDuplexOptions { InputDevice = Mic("wired", AudioDeviceType.WiredHeadset) });
        var raised = 0;
        session.InputDeviceChanged += (s, _) =>
        {
            raised++;
            if (!ReferenceEquals(s, session))
                throw new Exception("Wrong sender");
        };

        session.LoseInput();
        session.LoseInput();   // already on the default: nothing to report

        await Assert.That(session.InputDevice).IsNull();
        await Assert.That(raised).IsEqualTo(1);
    }

    [Test]
    public async Task Session_InputDeviceUnavailableAtStart_IsSilent()
    {
        await using var session = new FakeSession(new AudioDuplexOptions { InputDevice = Mic("gone") });
        var raised = 0;
        session.InputDeviceChanged += (_, _) => raised++;

        session.LoseInput(notify: false);

        await Assert.That(session.InputDevice).IsNull();
        await Assert.That(raised).IsEqualTo(0);
    }

    [Test]
    public async Task Session_InputDevice_AfterStop_IsStoredButNotForwarded()
    {
        var session = new FakeSession(new AudioDuplexOptions());
        var raised = 0;
        session.InputDeviceChanged += (_, _) => raised++;
        await session.StopAsync();

        session.InputDevice = Mic("late");
        session.LoseInput();

        await Assert.That(session.Selected.Count).IsEqualTo(0);
        await Assert.That(raised).IsEqualTo(0);
    }

    [Test]
    public async Task Session_StopFailure_IsReported_ButStillCompletes()
    {
        var session = new FakeSession(new AudioDuplexOptions(), () => throw new InvalidOperationException("boom"));

        await Assert.That(session.StopAsync).Throws<InvalidOperationException>();
        await Assert.That(session.Captured.Completion.IsCompleted).IsTrue();
    }


    // ---- one session at a time -------------------------------------------------------------------

    [Test]
    public async Task Slot_SecondStart_Throws_UntilTheFirstStops()
    {
        var slot = new AudioDuplexSlot();
        Task<AudioDuplexSession> Start(AudioDuplexOptions o, CancellationToken _) => Task.FromResult<AudioDuplexSession>(new FakeSession(o));

        var first = await slot.StartAsync(null, default, Start);
        await Assert.That(slot.Current).IsSameReferenceAs(first);
        await Assert.That(() => (Task)slot.StartAsync(null, default, Start)).Throws<InvalidOperationException>();

        await first.StopAsync();
        await Assert.That(slot.Current).IsNull();

        var again = await slot.StartAsync(new AudioDuplexOptions { SampleRate = 8000 }, default, Start);
        await Assert.That(again.SampleRate).IsEqualTo(8000);
        await again.DisposeAsync();
    }

    [Test]
    public async Task Slot_FailedStart_FreesTheSlot()
    {
        var slot = new AudioDuplexSlot();

        await Assert.That(() => (Task)slot.StartAsync(null, default, (_, _) => throw new InvalidOperationException("no mic")))
            .Throws<InvalidOperationException>();

        var session = await slot.StartAsync(null, default, (o, _) => Task.FromResult<AudioDuplexSession>(new FakeSession(o)));
        await Assert.That(slot.Current).IsNotNull();
        await session.StopAsync();
    }

    [Test]
    public async Task Slot_InvalidOptions_AreRejectedBeforeStarting()
    {
        var slot = new AudioDuplexSlot();
        var started = false;

        await Assert.That(() => (Task)slot.StartAsync(new AudioDuplexOptions { SampleRate = 48000 }, default, (o, _) =>
        {
            started = true;
            return Task.FromResult<AudioDuplexSession>(new FakeSession(o));
        })).Throws<ArgumentException>();

        await Assert.That(started).IsFalse();
        await Assert.That(slot.Current).IsNull();
    }
}
