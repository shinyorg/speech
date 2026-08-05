using Shiny.Speech;

namespace Shiny.Speech.Tests;

/// <summary>
/// Annotations are recognized by shape rather than a fixed vocabulary, so the interesting cases are
/// the boundaries: what counts as a tag, what survives stripping, and how a tone is projected for a
/// provider that can't perform tags inline.
/// </summary>
public class SpeechAnnotationsTests
{
    const SpeechToneCapabilities Inline = SpeechToneCapabilities.InlineAnnotations;
    const SpeechToneCapabilities Native = SpeechToneCapabilities.Emotion | SpeechToneCapabilities.Intensity;

    [Test]
    public async Task Strip_RemovesLeadingTagAndItsWhitespace()
        => await Assert.That(SpeechAnnotations.Strip("[excited] We shipped it."))
            .IsEqualTo("We shipped it.");

    [Test]
    public async Task Strip_DoesNotLeaveDoubleSpacesMidSentence()
        => await Assert.That(SpeechAnnotations.Strip("We shipped it [laughs] finally."))
            .IsEqualTo("We shipped it finally.");

    [Test]
    public async Task Strip_HandlesMultiWordTags()
        => await Assert.That(SpeechAnnotations.Strip("[starts laughing] No way. [door slams]"))
            .IsEqualTo("No way.");

    [Test]
    public async Task Strip_LeavesNonAnnotationBracketsAlone()
    {
        // Digits, punctuation and long runs of words are prose, not performance direction.
        await Assert.That(SpeechAnnotations.Strip("See footnote [1] for details."))
            .IsEqualTo("See footnote [1] for details.");

        await Assert.That(SpeechAnnotations.Strip("The array [a, b] is sorted."))
            .IsEqualTo("The array [a, b] is sorted.");

        await Assert.That(SpeechAnnotations.Strip("[this is far too many words to be a tag] Hello."))
            .IsEqualTo("[this is far too many words to be a tag] Hello.");
    }

    [Test]
    public async Task Strip_IgnoresUnclosedBracket()
        => await Assert.That(SpeechAnnotations.Strip("Wait [excited and then nothing"))
            .IsEqualTo("Wait [excited and then nothing");

    [Test]
    public async Task Extract_ReturnsTagsInOrder()
    {
        var tags = SpeechAnnotations.Extract("[excited] Yes! [laughs] Really.");

        await Assert.That(tags.Count).IsEqualTo(2);
        await Assert.That(tags[0]).IsEqualTo("excited");
        await Assert.That(tags[1]).IsEqualTo("laughs");
    }

    [Test]
    public async Task Resolve_PromotesFirstEmotionTag()
    {
        var result = SpeechAnnotations.Resolve("[excited] We shipped it.", null, Native);

        await Assert.That(result.Text).IsEqualTo("We shipped it.");
        await Assert.That(result.Tone!.Emotion).IsEqualTo(SpeechEmotion.Excited);
    }

    [Test]
    public async Task Resolve_SkipsPerformanceBeatsWhenPromoting()
    {
        // [laughs] is a beat with no portable tone; the following emotion word is what promotes.
        var result = SpeechAnnotations.Resolve("[laughs] [sad] Oh well.", null, Native);

        await Assert.That(result.Text).IsEqualTo("Oh well.");
        await Assert.That(result.Tone!.Emotion).IsEqualTo(SpeechEmotion.Sad);
    }

    [Test]
    public async Task Resolve_ExplicitToneBeatsInlineTag()
    {
        var options = new TextToSpeechOptions { Tone = new SpeechTone { Emotion = SpeechEmotion.Calm } };
        var result = SpeechAnnotations.Resolve("[angry] Please sit down.", options, Native);

        await Assert.That(result.Tone!.Emotion).IsEqualTo(SpeechEmotion.Calm);
    }

    [Test]
    public async Task Resolve_KeepsTagsForInlineCapableProvider()
    {
        var result = SpeechAnnotations.Resolve("[excited] We shipped it.", null, Inline);

        await Assert.That(result.Text).IsEqualTo("[excited] We shipped it.");
        await Assert.That(result.Tone!.Emotion).IsEqualTo(SpeechEmotion.Excited);
    }

    [Test]
    public async Task Resolve_EmitsTagForExplicitToneOnInlineProvider()
    {
        var options = new TextToSpeechOptions { Tone = new SpeechTone { Emotion = SpeechEmotion.Whispering } };
        var result = SpeechAnnotations.Resolve("Don't wake them.", options, Inline);

        await Assert.That(result.Text).IsEqualTo("[whispers] Don't wake them.");
    }

    [Test]
    public async Task Resolve_DoesNotDoubleTagWhenTonePromotedFromText()
    {
        var result = SpeechAnnotations.Resolve("[sad] Oh well.", null, Inline);

        await Assert.That(result.Text).IsEqualTo("[sad] Oh well.");
    }

    [Test]
    public async Task Resolve_StripsForProviderWithNoToneSupport()
    {
        var result = SpeechAnnotations.Resolve("[excited] We shipped it.", null, SpeechToneCapabilities.None);

        await Assert.That(result.Text).IsEqualTo("We shipped it.");
    }

    [Test]
    public async Task Resolve_PreserveLeavesTextUntouched()
    {
        var options = new TextToSpeechOptions { AnnotationHandling = SpeechAnnotationHandling.Preserve };
        var result = SpeechAnnotations.Resolve("[excited] We shipped it.", options, SpeechToneCapabilities.None);

        await Assert.That(result.Text).IsEqualTo("[excited] We shipped it.");
        await Assert.That(result.Tone).IsNull();
    }

    [Test]
    public async Task Resolve_StripHandlingDoesNotPromote()
    {
        var options = new TextToSpeechOptions { AnnotationHandling = SpeechAnnotationHandling.Strip };
        var result = SpeechAnnotations.Resolve("[excited] We shipped it.", options, Inline);

        await Assert.That(result.Text).IsEqualTo("We shipped it.");
        await Assert.That(result.Tone).IsNull();
    }

    [Test]
    public async Task Resolve_NeutralToneWithNoInstructionsCollapsesToNull()
    {
        var options = new TextToSpeechOptions { Tone = new SpeechTone { Emotion = SpeechEmotion.Neutral } };
        var result = SpeechAnnotations.Resolve("Hello.", options, Native);

        await Assert.That(result.Tone).IsNull();
    }

    [Test]
    public async Task Resolve_NeutralToneWithInstructionsSurvives()
    {
        // Free-text direction is meaningful on its own — OpenAI takes it without any emotion.
        var options = new TextToSpeechOptions
        {
            Tone = new SpeechTone { Instructions = "Sound like a sports announcer." }
        };
        var result = SpeechAnnotations.Resolve("Hello.", options, SpeechToneCapabilities.Instructions);

        await Assert.That(result.Tone).IsNotNull();
        await Assert.That(result.Tone!.Instructions).IsEqualTo("Sound like a sports announcer.");
    }

    [Test]
    public async Task ToEmotion_IsCaseInsensitiveAndReturnsNullForBeats()
    {
        await Assert.That(SpeechAnnotations.ToEmotion("WHISPERS")).IsEqualTo(SpeechEmotion.Whispering);
        await Assert.That(SpeechAnnotations.ToEmotion("laughs")).IsNull();
        await Assert.That(SpeechAnnotations.ToEmotion("gunshot")).IsNull();
    }

    [Test]
    public async Task ToAnnotation_RoundTripsThroughToEmotion()
    {
        foreach (var emotion in Enum.GetValues<SpeechEmotion>())
        {
            var annotation = SpeechAnnotations.ToAnnotation(emotion);
            if (emotion == SpeechEmotion.Neutral)
            {
                await Assert.That(annotation).IsNull();
                continue;
            }

            await Assert.That(annotation).IsNotNull();
            await Assert.That(SpeechAnnotations.ToEmotion(annotation!)).IsEqualTo(emotion);
        }
    }
}
