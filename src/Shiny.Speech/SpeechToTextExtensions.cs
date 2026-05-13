using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Shiny.Speech;

public static class SpeechToTextExtensions
{
    public static async Task<string?> ListenUntilSilence(
        this ISpeechToTextService service,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var tcs = new TaskCompletionSource<string?>();
        string? lastText = null;

        void OnResult(object? sender, SpeechRecognitionResult result)
        {
            lastText = result.Text;
            if (result.IsFinal)
                tcs.TrySetResult(result.Text);
        }

        void OnError(object? sender, SpeechRecognitionError error)
        {
            tcs.TrySetException(error.Exception ?? new InvalidOperationException(error.Message));
        }

        service.ResultReceived += OnResult;
        service.Error += OnError;

        await using var reg = cancellationToken.Register(() => tcs.TrySetResult(lastText));

        try
        {
            if (!service.IsListening)
                await service.Start(options);

            return await tcs.Task;
        }
        finally
        {
            service.ResultReceived -= OnResult;
            service.Error -= OnError;
            await service.Stop();
        }
    }

    public static async Task<string?> StatementAfterKeyword(
        this ISpeechToTextService service,
        string[] keywords,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(keywords);
        if (keywords.Length == 0)
            throw new ArgumentException("At least one keyword is required.", nameof(keywords));

        options = (options ?? new SpeechRecognitionOptions()) with { Keywords = keywords };

        var tcs = new TaskCompletionSource<string?>();
        var keywordHeard = false;

        void OnKeyword(object? sender, string keyword) => keywordHeard = true;

        void OnResult(object? sender, SpeechRecognitionResult result)
        {
            if (!keywordHeard || !result.IsFinal)
                return;

            var text = result.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                tcs.TrySetResult(text);
        }

        void OnError(object? sender, SpeechRecognitionError error)
        {
            tcs.TrySetException(error.Exception ?? new InvalidOperationException(error.Message));
        }

        service.KeywordHeard += OnKeyword;
        service.ResultReceived += OnResult;
        service.Error += OnError;

        await using var reg = cancellationToken.Register(() => tcs.TrySetResult(null));

        try
        {
            if (!service.IsListening)
                await service.Start(options);

            return await tcs.Task;
        }
        finally
        {
            service.KeywordHeard -= OnKeyword;
            service.ResultReceived -= OnResult;
            service.Error -= OnError;
            await service.Stop();
        }
    }

    public static async Task<string?> WaitListenForKeywords(
        this ISpeechToTextService service,
        string[] keywords,
        TimeSpan? timeout = null,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(keywords);
        if (keywords.Length == 0)
            throw new ArgumentException("At least one keyword is required.", nameof(keywords));

        options = (options ?? new SpeechRecognitionOptions()) with { Keywords = keywords };

        var tcs = new TaskCompletionSource<string?>();

        void OnKeyword(object? sender, string keyword) => tcs.TrySetResult(keyword);

        void OnError(object? sender, SpeechRecognitionError error)
        {
            tcs.TrySetException(error.Exception ?? new InvalidOperationException(error.Message));
        }

        service.KeywordHeard += OnKeyword;
        service.Error += OnError;

        using var cts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (timeout.HasValue)
            cts!.CancelAfter(timeout.Value);

        var effectiveToken = cts?.Token ?? cancellationToken;
        await using var reg = effectiveToken.Register(() => tcs.TrySetResult(null));

        try
        {
            if (!service.IsListening)
                await service.Start(options);

            return await tcs.Task;
        }
        finally
        {
            service.KeywordHeard -= OnKeyword;
            service.Error -= OnError;
            await service.Stop();
        }
    }

    public static async IAsyncEnumerable<string> ListenForKeywords(
        this ISpeechToTextService service,
        string[] keywords,
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(keywords);
        if (keywords.Length == 0)
            throw new ArgumentException("At least one keyword is required.", nameof(keywords));

        options = (options ?? new SpeechRecognitionOptions()) with { Keywords = keywords };

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        void OnKeyword(object? sender, string keyword) => channel.Writer.TryWrite(keyword);

        void OnError(object? sender, SpeechRecognitionError error)
        {
            channel.Writer.TryComplete(error.Exception ?? new InvalidOperationException(error.Message));
        }

        service.KeywordHeard += OnKeyword;
        service.Error += OnError;

        try
        {
            if (!service.IsListening)
                await service.Start(options);

            await using var reg = cancellationToken.Register(() => channel.Writer.TryComplete());

            await foreach (var keyword in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return keyword;
            }
        }
        finally
        {
            service.KeywordHeard -= OnKeyword;
            service.Error -= OnError;
            await service.Stop();
        }
    }
}
