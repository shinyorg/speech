using Typecast.Models;

namespace Shiny.Speech.Typecast;

/// <summary>
/// Projects the portable <see cref="SpeechEmotion"/> onto Typecast's preset vocabulary
/// (<c>normal, happy, sad, angry, whisper, toneup, tonedown</c>). The mapping is lossy by
/// necessity — Typecast has no distinct "excited" or "sarcastic" preset — so several emotions
/// collapse onto the nearest available preset and a few have no equivalent at all.
/// </summary>
public static class TypecastEmotionMap
{
    /// <summary>
    /// The closest Typecast preset for <paramref name="emotion"/>, or null when Typecast offers
    /// nothing comparable (the caller should then fall back to its configured default).
    /// </summary>
    public static EmotionPreset? From(SpeechEmotion? emotion) => emotion switch
    {
        SpeechEmotion.Happy or SpeechEmotion.Excited or SpeechEmotion.Friendly => EmotionPreset.Happy,
        SpeechEmotion.Sad => EmotionPreset.Sad,
        SpeechEmotion.Angry => EmotionPreset.Angry,
        SpeechEmotion.Whispering => EmotionPreset.Whisper,
        SpeechEmotion.Shouting => EmotionPreset.ToneUp,
        SpeechEmotion.Calm or SpeechEmotion.Serious => EmotionPreset.ToneDown,
        SpeechEmotion.Neutral => EmotionPreset.Normal,

        // Fearful and Sarcastic have no honest analogue — leave them to the configured default
        // rather than picking a preset that would misrepresent the intent.
        _ => null
    };
}
