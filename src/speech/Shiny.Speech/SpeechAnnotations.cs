using System.Text;

namespace Shiny.Speech;

/// <summary>
/// The text and tone to actually synthesize, after annotation handling has been applied.
/// </summary>
/// <param name="Text">The text to send to the provider. Annotations are stripped unless the
/// provider reports <see cref="SpeechToneCapabilities.InlineAnnotations"/>.</param>
/// <param name="Tone">The effective tone, or null when there is no direction to apply.</param>
public record ResolvedSpeech(string Text, SpeechTone? Tone);

/// <summary>
/// Parses, strips and emits the bracketed annotations (<c>[excited]</c>, <c>[whispers]</c>,
/// <c>[laughs]</c>) that expressive TTS models use as performance direction, and normalizes them
/// against what a given provider can actually do.
/// <para>
/// Only ElevenLabs' <c>eleven_v3</c> interprets these tags; every other engine — including older
/// ElevenLabs models — speaks them aloud verbatim. Providers call
/// <see cref="Resolve"/> so a single piece of annotated text behaves sensibly everywhere.
/// </para>
/// </summary>
public static class SpeechAnnotations
{
    // Annotations are open-ended natural language, so they're recognized by shape rather than by a
    // fixed vocabulary: a short run of words in square brackets. Anything longer or containing
    // digits/punctuation is left alone so real bracketed prose survives.
    const int MaxAnnotationLength = 40;
    const int MaxAnnotationWords = 3;

    static readonly Dictionary<string, SpeechEmotion> EmotionTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["happy"] = SpeechEmotion.Happy,
        ["happily"] = SpeechEmotion.Happy,
        ["cheerful"] = SpeechEmotion.Happy,
        ["cheerfully"] = SpeechEmotion.Happy,
        ["joyful"] = SpeechEmotion.Happy,
        ["joyfully"] = SpeechEmotion.Happy,
        ["delighted"] = SpeechEmotion.Happy,
        ["pleased"] = SpeechEmotion.Happy,
        ["amused"] = SpeechEmotion.Happy,
        ["playful"] = SpeechEmotion.Happy,
        ["playfully"] = SpeechEmotion.Happy,
        ["mischievously"] = SpeechEmotion.Happy,

        ["excited"] = SpeechEmotion.Excited,
        ["excitedly"] = SpeechEmotion.Excited,
        ["enthusiastic"] = SpeechEmotion.Excited,
        ["enthusiastically"] = SpeechEmotion.Excited,
        ["eager"] = SpeechEmotion.Excited,
        ["eagerly"] = SpeechEmotion.Excited,
        ["exhilarated"] = SpeechEmotion.Excited,
        ["amazed"] = SpeechEmotion.Excited,

        ["sad"] = SpeechEmotion.Sad,
        ["sadly"] = SpeechEmotion.Sad,
        ["sorrowful"] = SpeechEmotion.Sad,
        ["sorrowfully"] = SpeechEmotion.Sad,
        ["melancholy"] = SpeechEmotion.Sad,
        ["mournful"] = SpeechEmotion.Sad,
        ["disappointed"] = SpeechEmotion.Sad,
        ["dejected"] = SpeechEmotion.Sad,
        ["tearful"] = SpeechEmotion.Sad,
        ["resigned"] = SpeechEmotion.Sad,

        ["angry"] = SpeechEmotion.Angry,
        ["angrily"] = SpeechEmotion.Angry,
        ["furious"] = SpeechEmotion.Angry,
        ["furiously"] = SpeechEmotion.Angry,
        ["irritated"] = SpeechEmotion.Angry,
        ["annoyed"] = SpeechEmotion.Angry,
        ["frustrated"] = SpeechEmotion.Angry,
        ["indignant"] = SpeechEmotion.Angry,

        ["fearful"] = SpeechEmotion.Fearful,
        ["afraid"] = SpeechEmotion.Fearful,
        ["scared"] = SpeechEmotion.Fearful,
        ["terrified"] = SpeechEmotion.Fearful,
        ["panicked"] = SpeechEmotion.Fearful,
        ["nervous"] = SpeechEmotion.Fearful,
        ["nervously"] = SpeechEmotion.Fearful,
        ["anxious"] = SpeechEmotion.Fearful,
        ["worried"] = SpeechEmotion.Fearful,
        ["trembling"] = SpeechEmotion.Fearful,

        ["calm"] = SpeechEmotion.Calm,
        ["calmly"] = SpeechEmotion.Calm,
        ["relaxed"] = SpeechEmotion.Calm,
        ["gentle"] = SpeechEmotion.Calm,
        ["gently"] = SpeechEmotion.Calm,
        ["soothing"] = SpeechEmotion.Calm,
        ["soothingly"] = SpeechEmotion.Calm,
        ["reassuring"] = SpeechEmotion.Calm,

        ["whisper"] = SpeechEmotion.Whispering,
        ["whispers"] = SpeechEmotion.Whispering,
        ["whispering"] = SpeechEmotion.Whispering,
        ["whispered"] = SpeechEmotion.Whispering,
        ["hushed"] = SpeechEmotion.Whispering,
        ["quietly"] = SpeechEmotion.Whispering,

        ["shout"] = SpeechEmotion.Shouting,
        ["shouts"] = SpeechEmotion.Shouting,
        ["shouting"] = SpeechEmotion.Shouting,
        ["yells"] = SpeechEmotion.Shouting,
        ["yelling"] = SpeechEmotion.Shouting,
        ["loudly"] = SpeechEmotion.Shouting,

        ["friendly"] = SpeechEmotion.Friendly,
        ["warm"] = SpeechEmotion.Friendly,
        ["warmly"] = SpeechEmotion.Friendly,
        ["kind"] = SpeechEmotion.Friendly,
        ["kindly"] = SpeechEmotion.Friendly,
        ["affectionate"] = SpeechEmotion.Friendly,
        ["affectionately"] = SpeechEmotion.Friendly,
        ["conversational"] = SpeechEmotion.Friendly,

        ["serious"] = SpeechEmotion.Serious,
        ["seriously"] = SpeechEmotion.Serious,
        ["stern"] = SpeechEmotion.Serious,
        ["sternly"] = SpeechEmotion.Serious,
        ["formal"] = SpeechEmotion.Serious,
        ["flatly"] = SpeechEmotion.Serious,
        ["deadpan"] = SpeechEmotion.Serious,
        ["solemn"] = SpeechEmotion.Serious,
        ["robotically"] = SpeechEmotion.Serious,

        ["sarcastic"] = SpeechEmotion.Sarcastic,
        ["sarcastically"] = SpeechEmotion.Sarcastic,
        ["sardonic"] = SpeechEmotion.Sarcastic,
        ["wry"] = SpeechEmotion.Sarcastic,
        ["wryly"] = SpeechEmotion.Sarcastic,
        ["ironic"] = SpeechEmotion.Sarcastic
    };

    /// <summary>
    /// Normalizes <paramref name="text"/> and <paramref name="options"/> against what
    /// <paramref name="capabilities"/> allows. See <see cref="SpeechAnnotationHandling"/> for the rules.
    /// </summary>
    public static ResolvedSpeech Resolve(
        string text,
        TextToSpeechOptions? options,
        SpeechToneCapabilities capabilities
    )
    {
        var handling = options?.AnnotationHandling ?? SpeechAnnotationHandling.Auto;
        var tone = Normalize(options?.Tone);

        switch (handling)
        {
            case SpeechAnnotationHandling.Preserve:
                return new ResolvedSpeech(text, tone);

            case SpeechAnnotationHandling.Strip:
                return new ResolvedSpeech(Strip(text), tone);
        }

        // Auto: promote the first emotion-bearing annotation when the caller gave no explicit tone.
        var promoted = false;
        if (tone == null)
        {
            foreach (var tag in Extract(text))
            {
                if (ToEmotion(tag) is { } emotion)
                {
                    tone = new SpeechTone { Emotion = emotion };
                    promoted = true;
                    break;
                }
            }
        }

        if (!capabilities.HasFlag(SpeechToneCapabilities.InlineAnnotations))
            return new ResolvedSpeech(Strip(text), tone);

        // The provider performs tags natively. Tags promoted out of the text are already in place;
        // an explicitly supplied tone is not, so lead with it.
        if (!promoted && ToAnnotation(tone?.Emotion ?? SpeechEmotion.Neutral) is { } annotation)
            return new ResolvedSpeech($"[{annotation}] {text}", tone);

        return new ResolvedSpeech(text, tone);
    }

    /// <summary>
    /// Removes every annotation from <paramref name="text"/>, along with the whitespace they leave
    /// behind, so a provider that would otherwise read "[excited]" aloud gets clean prose.
    /// </summary>
    public static string Strip(string text)
    {
        if (String.IsNullOrEmpty(text) || text.IndexOf('[') < 0)
            return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '[' && TryReadAnnotation(text, i, out var end, out _))
            {
                i = end + 1;

                // Collapse the gap the tag left: drop following whitespace when the tag sat at the
                // start of the text or was already preceded by whitespace.
                var precededByGap = sb.Length == 0 || Char.IsWhiteSpace(sb[sb.Length - 1]);
                if (precededByGap)
                {
                    while (i < text.Length && Char.IsWhiteSpace(text[i]))
                        i++;
                }
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Returns the contents of every annotation in <paramref name="text"/>, in order and without
    /// the surrounding brackets (e.g. <c>excited</c>, <c>laughs harder</c>).
    /// </summary>
    public static IReadOnlyList<string> Extract(string text)
    {
        if (String.IsNullOrEmpty(text) || text.IndexOf('[') < 0)
            return [];

        List<string>? tags = null;
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '[' && TryReadAnnotation(text, i, out var end, out var content))
            {
                (tags ??= []).Add(content);
                i = end + 1;
                continue;
            }
            i++;
        }

        return (IReadOnlyList<string>?)tags ?? [];
    }

    /// <summary>
    /// Maps an annotation's contents to a <see cref="SpeechEmotion"/>, or null when it carries no
    /// emotional direction a provider could reproduce — performance beats such as <c>[laughs]</c>
    /// and sound effects such as <c>[door slams]</c> have no portable equivalent.
    /// </summary>
    public static SpeechEmotion? ToEmotion(string annotation)
        => EmotionTags.TryGetValue(annotation.Trim(), out var emotion) ? emotion : null;

    /// <summary>
    /// The canonical annotation for <paramref name="emotion"/>, or null for
    /// <see cref="SpeechEmotion.Neutral"/> (which means "no direction").
    /// </summary>
    public static string? ToAnnotation(SpeechEmotion emotion) => emotion switch
    {
        SpeechEmotion.Happy => "happy",
        SpeechEmotion.Excited => "excited",
        SpeechEmotion.Sad => "sad",
        SpeechEmotion.Angry => "angry",
        SpeechEmotion.Fearful => "fearful",
        SpeechEmotion.Calm => "calm",
        SpeechEmotion.Whispering => "whispers",
        SpeechEmotion.Shouting => "shouts",
        SpeechEmotion.Friendly => "friendly",
        SpeechEmotion.Serious => "serious",
        SpeechEmotion.Sarcastic => "sarcastic",
        _ => null
    };

    /// <summary>
    /// Collapses a tone that carries no direction at all down to null, so providers only have one
    /// "nothing to do" case to check.
    /// </summary>
    internal static SpeechTone? Normalize(SpeechTone? tone)
    {
        if (tone == null)
            return null;

        if (tone.Emotion == SpeechEmotion.Neutral && String.IsNullOrWhiteSpace(tone.Instructions))
            return null;

        return tone;
    }

    static bool TryReadAnnotation(string text, int start, out int end, out string content)
    {
        end = -1;
        content = String.Empty;

        var words = 1;
        for (var i = start + 1; i < text.Length && i - start <= MaxAnnotationLength; i++)
        {
            var c = text[i];

            if (c == ']')
            {
                if (i == start + 1)
                    return false;

                content = text.Substring(start + 1, i - start - 1).Trim();
                if (content.Length == 0)
                    return false;

                end = i;
                return true;
            }

            if (c == ' ')
            {
                words++;
                if (words > MaxAnnotationWords)
                    return false;
                continue;
            }

            // Letters plus the joiners that show up in real tags ("laughs harder", "sing-song").
            if (!Char.IsLetter(c) && c != '-' && c != '\'')
                return false;
        }

        return false;
    }
}
