namespace Shiny.Speech.Azure;

/// <summary>
/// Projects the portable <see cref="SpeechEmotion"/> onto Azure's <c>mstts:express-as</c> style
/// names. Style support is per-voice — a voice that doesn't offer the requested style simply
/// renders in its default delivery, so an unmapped or unsupported style degrades quietly rather
/// than failing the request. Check the voice list for which styles a given neural voice supports.
/// </summary>
public static class AzureStyleMap
{
    /// <summary>
    /// The Azure style name for <paramref name="emotion"/>, or null when no
    /// <c>mstts:express-as</c> wrapper should be emitted.
    /// </summary>
    public static string? From(SpeechEmotion? emotion) => emotion switch
    {
        SpeechEmotion.Happy => "cheerful",
        SpeechEmotion.Excited => "excited",
        SpeechEmotion.Sad => "sad",
        SpeechEmotion.Angry => "angry",
        SpeechEmotion.Fearful => "terrified",
        SpeechEmotion.Calm => "calm",
        SpeechEmotion.Whispering => "whispering",
        SpeechEmotion.Shouting => "shouting",
        SpeechEmotion.Friendly => "friendly",
        SpeechEmotion.Serious => "serious",

        // Azure has no sarcasm style; "unfriendly" is the closest dry/edged delivery it offers.
        SpeechEmotion.Sarcastic => "unfriendly",
        _ => null
    };
}
