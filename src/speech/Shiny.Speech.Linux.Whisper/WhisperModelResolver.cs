using Microsoft.Extensions.Logging;
using Whisper.net.Ggml;

namespace Shiny.Speech.Linux;

/// <summary>
/// Resolves the ggml model file the provider loads — returning a path that already exists, or
/// downloading it from Hugging Face into the model cache first.
/// </summary>
public static class WhisperModelResolver
{
    /// <summary>
    /// The default model cache directory — <c>$XDG_DATA_HOME/shiny.speech/whisper</c>, which
    /// resolves to <c>~/.local/share/shiny.speech/whisper</c> on a stock Linux install.
    /// </summary>
    public static string DefaultModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "shiny.speech",
        "whisper"
    );

    /// <summary>
    /// The file name a given model/quantization pair is cached under — the same
    /// <c>ggml-base.en.bin</c> style whisper.cpp itself uses, suffixed with the quantization.
    /// </summary>
    public static string GetModelFileName(GgmlType type, QuantizationType quantization)
    {
        var name = type switch
        {
            GgmlType.Tiny => "ggml-tiny",
            GgmlType.TinyEn => "ggml-tiny.en",
            GgmlType.Base => "ggml-base",
            GgmlType.BaseEn => "ggml-base.en",
            GgmlType.Small => "ggml-small",
            GgmlType.SmallEn => "ggml-small.en",
            GgmlType.Medium => "ggml-medium",
            GgmlType.MediumEn => "ggml-medium.en",
            GgmlType.LargeV1 => "ggml-large-v1",
            GgmlType.LargeV2 => "ggml-large-v2",
            GgmlType.LargeV3 => "ggml-large-v3",
            GgmlType.LargeV3Turbo => "ggml-large-v3-turbo",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown ggml model type")
        };

        var suffix = quantization switch
        {
            QuantizationType.NoQuantization => "",
            QuantizationType.Q4_0 => "-q4_0",
            QuantizationType.Q4_1 => "-q4_1",
            QuantizationType.Q5_0 => "-q5_0",
            QuantizationType.Q5_1 => "-q5_1",
            QuantizationType.Q8_0 => "-q8_0",
            _ => throw new ArgumentOutOfRangeException(nameof(quantization), quantization, "Unknown quantization type")
        };

        return $"{name}{suffix}.bin";
    }

    /// <summary>
    /// Returns the local path of the configured model, downloading it first when it's missing and
    /// <see cref="WhisperConfig.AutoDownloadModel"/> is enabled.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// The model isn't on disk and auto-download is disabled.
    /// </exception>
    public static async Task<string> ResolveAsync(
        WhisperConfig config,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var path = config.ModelPath ?? Path.Combine(
            config.ModelDirectory ?? DefaultModelDirectory,
            GetModelFileName(config.ModelType, config.Quantization)
        );

        if (File.Exists(path))
            return path;

        if (!config.AutoDownloadModel)
        {
            throw new FileNotFoundException(
                $"Whisper model '{path}' was not found and {nameof(WhisperConfig.AutoDownloadModel)} is disabled. " +
                "Provision the ggml model file or enable auto-download.",
                path
            );
        }

        var directory = Path.GetDirectoryName(path);
        if (!String.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        logger.LogInformation(
            "Downloading whisper model {Model} ({Quantization}) to {Path} — this is a one-time cost of up to a few hundred MB",
            config.ModelType,
            config.Quantization,
            path
        );

        // Download to a temp file and move into place so a killed process (or a second one racing
        // us on the same cache directory) can never leave a truncated model that later fails to
        // load with an opaque native error.
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(config.ModelType, config.Quantization, cancellationToken))
            await using (var destination = File.Create(temp))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        logger.LogInformation("Whisper model downloaded to {Path}", path);
        return path;
    }
}
