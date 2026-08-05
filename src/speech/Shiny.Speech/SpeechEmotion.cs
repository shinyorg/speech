namespace Shiny.Speech;

/// <summary>
/// A portable emotional delivery for synthesized speech. Each provider maps these onto whatever
/// mechanism it actually offers (ElevenLabs inline audio tags, Typecast emotion presets, Azure
/// SSML <c>mstts:express-as</c> styles, OpenAI instructions), so the mapping is deliberately
/// lossy — pick the closest intent rather than the exact provider term.
/// </summary>
public enum SpeechEmotion
{
    /// <summary>No emotional direction — the provider's own default delivery.</summary>
    Neutral,
    Happy,
    Excited,
    Sad,
    Angry,
    Fearful,
    Calm,
    Whispering,
    Shouting,
    Friendly,
    Serious,
    Sarcastic
}
