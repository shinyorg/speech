using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Speech.Linux;
using Whisper.net.Ggml;

namespace Shiny;

public static class WhisperServiceCollectionExtensions
{
    /// <summary>
    /// Registers on-device, offline speech-to-text (<see cref="Shiny.Speech.ISpeechToTextService"/>)
    /// backed by Whisper running locally through whisper.cpp — the Linux answer to the native
    /// recognizers iOS, Android and Windows ship with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call <c>AddLinuxAudio()</c> <b>first</b>: recognition consumes the microphone through
    /// <see cref="Shiny.Audio.IAudioSource"/>, and <c>Shiny.Audio</c> registers no capture
    /// implementation on Linux.
    /// </para>
    /// <para>
    /// Registration is skipped entirely off Linux, so this is safe to call unconditionally from
    /// shared startup code — on Windows/macOS/mobile the native or cloud registrations win.
    /// </para>
    /// <para>
    /// This is speech-to-text only. Whisper is a recognition model; there is no Whisper TTS.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddLinuxAudio();
    /// services.AddLinuxWhisperSpeechToText(new WhisperConfig
    /// {
    ///     ModelType = GgmlType.BaseEn,
    ///     Quantization = QuantizationType.Q5_1
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddLinuxWhisperSpeechToText(
        this IServiceCollection services,
        WhisperConfig? config = null)
    {
        if (!OperatingSystem.IsLinux())
            return services;

        services.TryAddSingleton(config ?? new WhisperConfig());
        services.AddCloudSpeechToText<WhisperSpeechToTextProvider>();
        return services;
    }

    /// <summary>
    /// Registers on-device Whisper speech-to-text with a specific model size, leaving everything else
    /// at its default.
    /// </summary>
    /// <param name="modelType">
    /// The ggml model to run — <c>Tiny</c>/<c>Base</c> for a Raspberry Pi, larger on a desktop or
    /// server. The <c>*En</c> variants are more accurate than their multilingual counterparts when
    /// you only need English.
    /// </param>
    /// <param name="quantization">
    /// Optional quantization — smaller and faster at a modest accuracy cost, which is usually the
    /// right trade on ARM boards.
    /// </param>
    public static IServiceCollection AddLinuxWhisperSpeechToText(
        this IServiceCollection services,
        GgmlType modelType,
        QuantizationType quantization = QuantizationType.NoQuantization)
        => services.AddLinuxWhisperSpeechToText(new WhisperConfig
        {
            ModelType = modelType,
            Quantization = quantization
        });
}
