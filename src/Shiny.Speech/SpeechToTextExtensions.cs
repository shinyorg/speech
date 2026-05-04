using System.Text.RegularExpressions;

namespace Shiny.Speech;

public static class SpeechToTextExtensions
{
    public static async Task<string?> ListenWithWakeWord(
        this ISpeechToTextService service,
        string wakePhrase,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wakePhrase);

        var wakeDetected = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            await foreach (var result in service.ContinuousRecognize(options, cancellationToken))
            {
                var idx = result.Text.IndexOf(wakePhrase, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    wakeDetected = true;
                    var afterWake = result.Text[(idx + wakePhrase.Length)..].Trim();

                    if (!string.IsNullOrWhiteSpace(afterWake) && result.IsFinal)
                        return afterWake;
                }

                if (wakeDetected && result.IsFinal)
                {
                    // Wake phrase was detected in a previous partial but final result
                    // has no content after it - user said "Hey Siri" then paused.
                    // Break inner loop to restart listening for the actual command.
                    break;
                }
            }

            if (wakeDetected)
            {
                // Wake word was said, now capture the next utterance as the command
                var command = await service.ListenUntilSilence(options, cancellationToken);
                if (!string.IsNullOrWhiteSpace(command))
                    return command;

                // If they stayed silent, reset and listen for wake word again
                wakeDetected = false;
            }
        }

        return null;
    }

    public static async Task<string?> ListenForKeyword(
        this ISpeechToTextService service,
        IEnumerable<string> keywords,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var keywordList = keywords?.ToArray() ?? throw new ArgumentNullException(nameof(keywords));
        if (keywordList.Length == 0)
            throw new ArgumentException("At least one keyword is required.", nameof(keywords));

        // Map matched text back to original-cased keyword
        var keywordLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keywordList)
            keywordLookup.TryAdd(k, k);

        // Single compiled regex with alternation for all keywords
        var pattern = new Regex(
            @"\b(" + string.Join("|", keywordList.Select(Regex.Escape)) + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        while (!cancellationToken.IsCancellationRequested)
        {
            await foreach (var result in service.ContinuousRecognize(options, cancellationToken))
            {
                var match = pattern.Match(result.Text);
                if (match.Success && keywordLookup.TryGetValue(match.Value, out var keyword))
                    return keyword;
            }
        }

        return null;
    }
}
