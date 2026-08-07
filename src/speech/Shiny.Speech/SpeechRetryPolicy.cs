namespace Shiny.Speech;

/// <summary>
/// Backoff schedule used by the native recognizers when a continuous session hits a recoverable
/// platform error and has to re-arm.
/// <para>
/// Continuous recognition is a loop: the platform recognizer finishes an utterance and the service
/// starts it again. A transient failure in that loop (the mic taken by another capture, a busy
/// recognizer, a dropped network round trip) must not end the session — but re-arming immediately
/// and forever would spin. Each consecutive failure waits longer, and a run of
/// <see cref="MaxConsecutiveFailures"/> of them ends the session rather than looping silently.
/// Any successful result resets the count.
/// </para>
/// </summary>
public static class SpeechRetryPolicy
{
    /// <summary>
    /// Consecutive recoverable failures tolerated before a session is given up on. Reaching it
    /// means the failure is not transient, so the service stops and reports through
    /// <see cref="ISpeechToTextService.Error"/> instead of retrying indefinitely.
    /// </summary>
    public const int MaxConsecutiveFailures = 5;

    static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];

    /// <summary>
    /// How long to wait before the re-arm attempt that follows
    /// <paramref name="consecutiveFailures"/> failures. Zero for a session that has not failed,
    /// then 250ms doubling out to a 4 second ceiling.
    /// </summary>
    public static TimeSpan GetBackoff(int consecutiveFailures)
    {
        if (consecutiveFailures < 1)
            return TimeSpan.Zero;

        var index = Math.Min(consecutiveFailures, Delays.Length) - 1;
        return Delays[index];
    }

    /// <summary>
    /// True once <paramref name="consecutiveFailures"/> has reached
    /// <see cref="MaxConsecutiveFailures"/> and the session should stop rather than re-arm again.
    /// </summary>
    public static bool ShouldGiveUp(int consecutiveFailures)
        => consecutiveFailures >= MaxConsecutiveFailures;
}
