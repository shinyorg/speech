using Shiny.Audio.Infrastructure;

namespace Shiny.Audio.Tests;

/// <summary>
/// Covers the platform-agnostic half of concurrent playback: the registry that lets a player keep
/// several clips in flight, and the handle that stops exactly one of them.
/// </summary>
public class PlaybackTests
{
    [Test]
    public async Task Concurrent_ClipsAreTrackedIndependently()
    {
        var registry = new AudioPlaybackRegistry();

        var first = registry.Create("first.mp3");
        var second = registry.Create("second.mp3");
        first.OnStop(() => Task.CompletedTask);
        second.OnStop(() => Task.CompletedTask);

        await Assert.That(registry.IsPlaying).IsTrue();
        await Assert.That(registry.Active.Count).IsEqualTo(2);
        await Assert.That(registry.Active.Select(x => x.Source)).IsEquivalentTo(["first.mp3", "second.mp3"]);
    }

    [Test]
    public async Task Stop_LeavesTheOtherClipsPlaying()
    {
        var registry = new AudioPlaybackRegistry();
        var torndown = new List<string>();

        var first = registry.Create("first.mp3");
        var second = registry.Create("second.mp3");
        first.OnStop(() => { torndown.Add("first"); return Task.CompletedTask; });
        second.OnStop(() => { torndown.Add("second"); return Task.CompletedTask; });

        await first.StopAsync();

        await Assert.That(first.Completion.IsCompletedSuccessfully).IsTrue();
        await Assert.That(first.IsPlaying).IsFalse();
        await Assert.That(second.IsPlaying).IsTrue();
        await Assert.That(registry.Active.Count).IsEqualTo(1);
        await Assert.That(torndown).IsEquivalentTo(["first"]);
    }

    [Test]
    public async Task StopAll_TearsDownEveryClip()
    {
        var registry = new AudioPlaybackRegistry();
        var torndown = 0;

        for (var i = 0; i < 3; i++)
            registry.Create($"clip{i}.mp3").OnStop(() =>
            {
                Interlocked.Increment(ref torndown);
                return Task.CompletedTask;
            });

        await registry.StopAllAsync();

        await Assert.That(torndown).IsEqualTo(3);
        await Assert.That(registry.IsPlaying).IsFalse();
        await Assert.That(registry.Active.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Teardown_RunsOnlyOnce()
    {
        var registry = new AudioPlaybackRegistry();
        var count = 0;

        var playback = registry.Create(null);
        playback.OnStop(() => { Interlocked.Increment(ref count); return Task.CompletedTask; });

        await Task.WhenAll(playback.StopAsync(), playback.StopAsync(), playback.StopAsync());
        playback.Complete();
        await playback.Completion;

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task Complete_FinishesTheClipAtItsNaturalEnd()
    {
        var registry = new AudioPlaybackRegistry();
        var playback = registry.Create("clip.mp3");
        playback.OnStop(() => Task.CompletedTask);

        playback.Complete();
        await playback.Completion;

        await Assert.That(playback.IsPlaying).IsFalse();
        await Assert.That(registry.IsPlaying).IsFalse();
    }

    [Test]
    public async Task Fail_SurfacesOnCompletion()
    {
        var registry = new AudioPlaybackRegistry();
        var playback = registry.Create("broken.mp3");
        playback.OnStop(() => Task.CompletedTask);

        playback.Fail(new InvalidOperationException("boom"));

        await Assert.That(async () => await playback.Completion)
            .Throws<InvalidOperationException>()
            .WithMessage("boom");

        await Assert.That(registry.Active.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Cancellation_StopsOnlyTheLinkedClip()
    {
        var registry = new AudioPlaybackRegistry();
        using var cts = new CancellationTokenSource();

        var cancelled = registry.Create("cancelled.mp3");
        var other = registry.Create("other.mp3");
        cancelled.OnStop(() => Task.CompletedTask);
        other.OnStop(() => Task.CompletedTask);
        cancelled.CancelWith(cts.Token);

        await cts.CancelAsync();
        await cancelled.Completion;   // cancellation completes normally, it does not throw

        await Assert.That(cancelled.IsPlaying).IsFalse();
        await Assert.That(other.IsPlaying).IsTrue();
    }

    [Test]
    public async Task Levels_ReportTheLoudestClip()
    {
        var registry = new AudioPlaybackRegistry();
        var quiet = registry.Create("quiet.mp3");
        var loud = registry.Create("loud.mp3");
        quiet.OnStop(() => Task.CompletedTask);
        loud.OnStop(() => Task.CompletedTask);

        await Assert.That(registry.ReportLevel(quiet, 0.2)).IsEqualTo(0.2);
        await Assert.That(registry.ReportLevel(loud, 0.9)).IsEqualTo(0.9);

        // The quiet clip reporting again must not pull the meter down while the loud one plays on.
        await Assert.That(registry.ReportLevel(quiet, 0.1)).IsEqualTo(0.9);

        await loud.StopAsync();
        await Assert.That(registry.ReportLevel(quiet, 0.1)).IsEqualTo(0.1);
    }

    [Test]
    public async Task DisposeAsync_StopsTheClip()
    {
        var registry = new AudioPlaybackRegistry();
        var stopped = false;

        var playback = registry.Create("clip.mp3");
        playback.OnStop(() => { stopped = true; return Task.CompletedTask; });

        await playback.DisposeAsync();

        await Assert.That(stopped).IsTrue();
        await Assert.That(registry.IsPlaying).IsFalse();
    }
}
