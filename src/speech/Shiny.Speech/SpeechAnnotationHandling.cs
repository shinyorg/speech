namespace Shiny.Speech;

/// <summary>
/// Controls what happens to bracketed annotations (<c>[excited]</c>, <c>[laughs]</c>) found in the
/// text passed to <see cref="ITextToSpeechService.SpeakAsync"/>.
/// </summary>
public enum SpeechAnnotationHandling
{
    /// <summary>
    /// Default. The first annotation that maps to a <see cref="SpeechEmotion"/> is promoted to a
    /// <see cref="SpeechTone"/> when the caller didn't supply one. Annotations are then kept in the
    /// text for providers that understand them (<see cref="SpeechToneCapabilities.InlineAnnotations"/>)
    /// and stripped for everyone else, so no provider ever speaks "[excited]" aloud.
    /// </summary>
    Auto,

    /// <summary>
    /// Pass the text through untouched — no promotion, no stripping. Use when the text legitimately
    /// contains square brackets, or when you're targeting one provider and want full control.
    /// </summary>
    Preserve,

    /// <summary>
    /// Always remove annotations, even on providers that support them, and never promote them to a
    /// tone. An explicit <see cref="TextToSpeechOptions.Tone"/> still applies.
    /// </summary>
    Strip
}
