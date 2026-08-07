namespace Shiny.Speech.Tests;

/// <summary>
/// The schedule is what stops a continuous session from either dying on the first transient
/// failure or spinning on a permanent one, so the boundaries are what matter: the first retry,
/// the ceiling, and where "keep trying" turns into "give up".
/// </summary>
public class SpeechRetryPolicyTests
{
    [Test]
    public async Task GetBackoff_HealthySessionDoesNotWait()
    {
        await Assert.That(SpeechRetryPolicy.GetBackoff(0)).IsEqualTo(TimeSpan.Zero);
        await Assert.That(SpeechRetryPolicy.GetBackoff(-1)).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task GetBackoff_FirstFailureRetriesAlmostImmediately()
        // A dropped utterance should not cost the user a noticeable gap in a live session.
        => await Assert.That(SpeechRetryPolicy.GetBackoff(1)).IsEqualTo(TimeSpan.FromMilliseconds(250));

    [Test]
    public async Task GetBackoff_GrowsWithEachConsecutiveFailure()
    {
        var previous = SpeechRetryPolicy.GetBackoff(1);

        for (var failures = 2; failures <= SpeechRetryPolicy.MaxConsecutiveFailures; failures++)
        {
            var current = SpeechRetryPolicy.GetBackoff(failures);
            await Assert.That(current).IsGreaterThan(previous);
            previous = current;
        }
    }

    [Test]
    public async Task GetBackoff_IsCappedRatherThanUnbounded()
    {
        var ceiling = SpeechRetryPolicy.GetBackoff(SpeechRetryPolicy.MaxConsecutiveFailures);

        await Assert.That(ceiling).IsEqualTo(TimeSpan.FromSeconds(4));
        // Past the table the delay holds at the ceiling instead of throwing or growing forever.
        await Assert.That(SpeechRetryPolicy.GetBackoff(50)).IsEqualTo(ceiling);
    }

    [Test]
    public async Task ShouldGiveUp_OnlyOnceTheFailuresStopLookingTransient()
    {
        await Assert.That(SpeechRetryPolicy.ShouldGiveUp(0)).IsFalse();
        await Assert.That(SpeechRetryPolicy.ShouldGiveUp(SpeechRetryPolicy.MaxConsecutiveFailures - 1)).IsFalse();
        await Assert.That(SpeechRetryPolicy.ShouldGiveUp(SpeechRetryPolicy.MaxConsecutiveFailures)).IsTrue();
        await Assert.That(SpeechRetryPolicy.ShouldGiveUp(SpeechRetryPolicy.MaxConsecutiveFailures + 1)).IsTrue();
    }

    [Test]
    public async Task TotalWaitBeforeGivingUp_StaysWithinASensibleWindow()
    {
        // Every attempt that will ever run, summed — a session should not sit "recovering" for
        // minutes before it admits defeat, nor bail inside a second.
        var total = TimeSpan.Zero;
        for (var failures = 1; failures < SpeechRetryPolicy.MaxConsecutiveFailures; failures++)
            total += SpeechRetryPolicy.GetBackoff(failures);

        await Assert.That(total).IsGreaterThan(TimeSpan.FromSeconds(1));
        await Assert.That(total).IsLessThan(TimeSpan.FromSeconds(10));
    }
}
