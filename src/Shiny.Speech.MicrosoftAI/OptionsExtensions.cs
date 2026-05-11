using System.Globalization;
using Microsoft.Extensions.AI;

using MeaiTextToSpeechOptions = Microsoft.Extensions.AI.TextToSpeechOptions;
using ShinyTextToSpeechOptions = Shiny.Speech.TextToSpeechOptions;

namespace Shiny.Speech.MicrosoftAI;

internal static class OptionsExtensions
{
    public static SpeechRecognitionOptions? ToSpeechRecognitionOptions(this SpeechToTextOptions? options)
    {
        if (options is null)
            return null;

        return new SpeechRecognitionOptions
        {
            Culture = options.SpeechLanguage != null
                ? new CultureInfo(options.SpeechLanguage)
                : null
        };
    }

    public static ShinyTextToSpeechOptions? ToShinyTextToSpeechOptions(this MeaiTextToSpeechOptions? options)
    {
        if (options is null)
            return null;

        VoiceInfo? voice = null;
        if (options.VoiceId != null)
            voice = new VoiceInfo(options.VoiceId, options.VoiceId, CultureInfo.InvariantCulture);

        return new ShinyTextToSpeechOptions
        {
            Culture = options.Language != null
                ? new CultureInfo(options.Language)
                : null,
            Voice = voice,
            SpeechRate = options.Speed ?? 1.0f,
            Pitch = options.Pitch ?? 1.0f,
            Volume = options.Volume ?? 1.0f
        };
    }
}
