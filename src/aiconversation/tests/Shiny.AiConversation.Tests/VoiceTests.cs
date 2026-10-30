using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AiConversation.Infrastructure;
using Shiny.AiConversation.Voice;
using Shiny.AiConversation.Voice.Infrastructure;
using Shiny.Speech;
using static Shiny.AiConversation.Tests.FakeChatClient;

namespace Shiny.AiConversation.Tests;

public class SentenceChunkerTests
{
    static List<string> Chunk(params string[] deltas)
    {
        var c = new SentenceChunker();
        var all = new List<string>();
        foreach (var d in deltas)
            all.AddRange(c.Add(d));
        all.AddRange(c.Flush());
        return all;
    }

    [Test]
    public async Task SplitsAtSentenceEnds_AcrossDeltas()
        => await Assert.That(String.Join("|", Chunk("Hel", "lo there. How ", "are you? Gre", "at!"))).IsEqualTo("Hello there.|How are you?|Great!");

    [Test]
    public async Task KeepsDecimals_ListNumbers_Abbreviations_AndInitials()
        => await Assert.That(String.Join("|", Chunk("It costs 3.50 dollars, e.g. at Dr. Smith's. J. R. agrees. Next"))).IsEqualTo("It costs 3.50 dollars, e.g. at Dr. Smith's.|J. R. agrees.|Next");

    [Test]
    public async Task LineBreaksEndSentences_AndCodeFencesAreDropped()
        => await Assert.That(String.Join("|", Chunk("Steps:\n1. Open it\n2. Run\n```csharp\nvar x = 1;\n```\nDone."))).IsEqualTo("Steps:|1. Open it|2. Run|Done.");

    [Test]
    public async Task StripMarkdown_ReadsNaturally()
    {
        await Assert.That(SpeechText.StripMarkdown("## **Bold** and _italic_ with `code` and [a link](https://x.y)")).IsEqualTo("Bold and italic with code and a link");
        await Assert.That(SpeechText.StripMarkdown("- item one")).IsEqualTo("item one");
        await Assert.That(SpeechText.StripMarkdown("1. first")).IsEqualTo("first");
        await Assert.That(SpeechText.StripMarkdown("| a | b |")).IsEqualTo("a b");
    }
}


public class VoiceConversationTests
{
    static VoiceOptions Opts(InterruptionMode mode = InterruptionMode.QuietWords, string? wakeWord = null, TimeSpan? followUp = null, AiAcknowledgement ack = AiAcknowledgement.Full)
        => new()
        {
            Interruption = mode,
            WakeWord = wakeWord,
            FollowUpTimeout = followUp ?? TimeSpan.FromSeconds(5),
            Acknowledgement = ack,
            EchoSettleWindow = TimeSpan.FromMilliseconds(50)
        };


    [Test]
    public async Task PushToTalk_SendsWhatWasHeard_AndSpeaksSentenceBySentence()
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("It is sunny. "), Text("Take a **hat**!"));
        await h.Start(Opts());

        await h.WaitForState(VoiceState.Off);
        await Assert.That(h.Stt.Starts.Count).IsEqualTo(0);   // no wake word: mic closed until asked

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Partial("what's the");
        h.Stt.Final("what's the weather");

        await VoiceHarness.WaitUntil(() => h.Tts.Spoken.Count == 2);
        await Assert.That(String.Join("|", h.Tts.Spoken)).IsEqualTo("It is sunny.|Take a hat!");
        await h.WaitForState(VoiceState.Off);

        // echo cancellation on for QuietWords (D15); voice input asks for spoken-style replies
        await Assert.That(h.Stt.Starts[0]!.AudioProcessing!.EchoCancellation).IsTrue();
        await Assert.That(h.Chat.Requests[0].Messages[0].Text.Contains("spoken aloud")).IsTrue();

        var messages = await h.Messages();
        await Assert.That(messages[0].Source).IsEqualTo(MessageSource.Voice);
        await Assert.That(messages[0].Text).IsEqualTo("what's the weather");

        var events = h.VoiceEvents;
        await Assert.That(events.OfType<InterimTranscript>().Any(x => x.Text == "what's the")).IsTrue();
        await Assert.That(events.OfType<FinalTranscript>().Single().Text).IsEqualTo("what's the weather");
        await Assert.That(events.OfType<ResponseLatency>().Count()).IsEqualTo(1);
        await Assert.That(h.Sounds.Played.Contains(AiAction.Ok) && h.Sounds.Played.Contains(AiAction.Think)).IsTrue();
        await Assert.That(h.Stt.Stops >= 1).IsTrue();          // closed again afterwards
    }


    [Test]
    public async Task Question_OpensFollowUp_WithoutWakeWord_ThenTimesOut()   // L5
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("Hot or iced?"), AskUser("c1", """[{ "id": "temp", "text": "Hot or iced?" }]"""));
        h.Chat.Reply(Text("Iced it is. Anything else?"));
        await h.Start(Opts(followUp: TimeSpan.FromMilliseconds(300)));

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("a coffee please");

        await h.WaitForState(VoiceState.FollowUp);
        h.Stt.Final("iced");
        await VoiceHarness.WaitUntil(() => h.Chat.Requests.Count == 2);
        await Assert.That(h.Chat.Requests[1].Messages.Texts(ChatRole.User).Last()).IsEqualTo("iced");

        // the heuristic question opens another follow-up, which nobody answers
        await h.WaitForState(VoiceState.FollowUp);
        await h.WaitForState(VoiceState.Off, 3000);
        await Assert.That(h.Chat.Requests.Count).IsEqualTo(2);
    }


    [Test]
    public async Task WakeWord_ListensContinuously_AndIgnoresSpeechBeforeTheWakeWord()
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("Hi!"));
        await h.Start(Opts(wakeWord: "hey shiny"));

        await h.WaitForState(VoiceState.WaitingForWakeWord);
        await Assert.That(h.Stt.Starts.Single()!.Keywords!.Contains("hey shiny")).IsTrue();

        h.Stt.Final("people chatting in the room");
        await Task.Delay(50);
        await Assert.That(h.Chat.Requests.Count).IsEqualTo(0);

        h.Stt.Keyword("hey shiny");
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("hello");

        await VoiceHarness.WaitUntil(() => h.Tts.Spoken.Count == 1);
        await h.WaitForState(VoiceState.WaitingForWakeWord);
        await Assert.That(h.Stt.Stops).IsEqualTo(0);              // one long-lived session (P8)
        await Assert.That(h.Chat.Requests[0].Messages.Texts(ChatRole.User).Single()).IsEqualTo("hello");
    }


    [Test]
    public async Task WakeWordDetector_KeepsTheRecognizerClosed_UntilTheWakeWord()
    {
        await using var h = new VoiceHarness(false, new FakeWakeWordDetector());
        h.Chat.Reply(Text("Hi!"));
        await h.Start(Opts(wakeWord: "hey shiny"));

        await h.WaitForState(VoiceState.WaitingForWakeWord);
        await VoiceHarness.WaitUntil(() => h.Detector!.Listens == 1);
        await Assert.That(h.Stt.IsListening).IsFalse();

        h.Detector!.Hear("hey shiny");
        await h.WaitForState(VoiceState.Listening);
        await Assert.That(h.Stt.Starts.Single()!.Keywords).IsNull();   // the recognizer isn't asked for the wake word
        h.Stt.Final("hello");

        await VoiceHarness.WaitUntil(() => h.Tts.Spoken.Count == 1);
        await h.WaitForState(VoiceState.WaitingForWakeWord);
        await VoiceHarness.WaitUntil(() => !h.Stt.IsListening);
    }


    [Test]
    public async Task QuietWord_StopsTheReply_OnlyWhenItIsTheWholeUtterance()   // L2
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("First sentence. Second sentence. Third sentence."));
        h.Tts.Hold();
        await h.Start(Opts());

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("tell me something");
        await h.WaitForState(VoiceState.Speaking);

        h.Stt.Partial("cancel this appointment");
        await Task.Delay(50);
        await Assert.That(h.Voice.State).IsEqualTo(VoiceState.Speaking);

        h.Stt.Partial("stop", confidence: 0);   // iOS-style placeholder confidence still counts (L3)
        await h.WaitForState(VoiceState.Off);

        await Assert.That(h.Tts.StopCalls >= 1).IsTrue();
        var interrupted = h.VoiceEvents.OfType<SpeechInterrupted>().Single();
        await Assert.That(interrupted.HeardText).IsEqualTo("stop");
        await Assert.That(interrupted.SpokenText).IsEqualTo("");   // nothing finished playing yet

        await h.SessionEvents.WaitFor(e => e.OfType<MessageUpdated>().Any(m => m.Message.Status == MessageStatus.Interrupted));
        await Assert.That(h.Sounds.Played.Contains(AiAction.Cancel)).IsTrue();
        await Assert.That(h.Chat.Requests.Count).IsEqualTo(1);
    }


    [Test]
    public async Task FullMode_NewSpeechInterrupts_AndBecomesTheNextMessage()   // L3, P3
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("Here is a long answer about coffee. It goes on."));
        h.Chat.Reply(Text("Tea it is."));
        h.Tts.Hold();
        await h.Start(Opts(InterruptionMode.Full));

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("a coffee");
        await h.WaitForState(VoiceState.Speaking);

        h.Stt.Final("blah echo", confidence: 0.2f);    // below the floor while speaking: treated as echo
        await Task.Delay(50);
        await Assert.That(h.Chat.Requests.Count).IsEqualTo(1);

        h.Tts.Release();
        h.Tts.Hold();
        await VoiceHarness.WaitUntil(() => h.Tts.Spoken.Count == 2);   // first sentence finished
        h.Stt.Final("actually make it tea", confidence: 0.9f);

        await VoiceHarness.WaitUntil(() => h.Chat.Requests.Count == 2);
        h.Tts.Release();
        var history = h.Chat.Requests[1].Messages;
        await Assert.That(history.Texts(ChatRole.User).Last()).IsEqualTo("actually make it tea");
        await Assert.That(history.Texts(ChatRole.Assistant).Single()).IsEqualTo($"Here is a long answer about coffee. {MessageMapper.InterruptedMarker}");
    }


    [Test]
    public async Task OffMode_MutesRecognitionWhileSpeaking_AndIgnoresQuietWords()   // L1, L4
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("Please hold on. I am speaking now. Is that okay?"));
        h.Tts.Hold();
        await h.Start(Opts(InterruptionMode.Off, followUp: TimeSpan.FromSeconds(2)));

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("hello");
        await h.WaitForState(VoiceState.Speaking);

        h.Stt.Partial("stop");
        h.Stt.Final("please hold on");   // the recognizer hearing the AI's own voice
        await Task.Delay(30);
        await Assert.That(h.Voice.State).IsEqualTo(VoiceState.Speaking);
        await Assert.That(h.Stt.Starts[0]!.AudioProcessing).IsNull();   // no echo cancellation forced in Off

        h.Tts.Release();
        await h.WaitForState(VoiceState.FollowUp);
        await Task.Delay(80);   // past the settle window
        h.Stt.Final("yes it is");

        await VoiceHarness.WaitUntil(() => h.Chat.Requests.Count == 2);
        await Assert.That(h.Chat.Requests[1].Messages.Texts(ChatRole.User).Last()).IsEqualTo("yes it is");
    }


    [Test]
    public async Task StopSpeaking_RecordsCompletedSentences_AsWhatWasHeard()
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("One. Two. Three."));
        h.Chat.Reply(Text("ok"));
        await h.Start(Opts());

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Tts.Hold();
        h.Stt.Final("count");
        await h.WaitForState(VoiceState.Speaking);
        h.Tts.Release();
        h.Tts.Hold();
        await VoiceHarness.WaitUntil(() => h.Tts.Spoken.Count == 2);

        await h.Voice.StopSpeaking();
        await h.WaitForState(VoiceState.Off);
        await Assert.That(h.VoiceEvents.OfType<SpeechInterrupted>().Single().SpokenText).IsEqualTo("One.");
        h.Tts.Release();

        await h.SessionEvents.WaitFor(e => e.OfType<MessageUpdated>().Any(m => m.Message.Status == MessageStatus.Interrupted));
        var reply = (await h.Messages()).Last(x => x.Role == MessageRole.Assistant);
        await Assert.That(reply.SpokenText).IsEqualTo("One.");
    }


    [Test]
    public async Task PushToTalk_WhileSpeaking_StopsAndListens()
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("A long reply."));
        h.Chat.Reply(Text("Sure."));
        h.Tts.Hold();
        await h.Start(Opts());

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("talk");
        await h.WaitForState(VoiceState.Speaking);

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Tts.Release();
        h.Stt.Final("wait, different question");

        await VoiceHarness.WaitUntil(() => h.Chat.Requests.Count == 2);
        await Assert.That(h.Chat.Requests[1].Messages.Texts(ChatRole.User).Last()).IsEqualTo("wait, different question");
    }


    [Test]
    public async Task ToolAcknowledgement_ForSlowTools_NeverForSilentOnes()
    {
        var weather = AIFunctionFactory.Create(() => "sunny", "weather");
        var interceptor = new RecordingInterceptor { OnLoaded = ctx => ctx.AddTool(weather) };
        await using var h = new VoiceHarness(false, interceptor);
        h.Chat.Reply(Call("c1", "weather"));
        h.Chat.Reply(Text("It's sunny."));
        h.Chat.Reply(AskUser("c2", """[{ "id": "q", "text": "Anything else?" }]"""), Text("Anything else?"));
        await h.Start(Opts(followUp: TimeSpan.FromMilliseconds(200)));

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("weather?");
        await VoiceHarness.WaitUntil(() => h.Tts.Spoken.Count == 2);
        await Assert.That(String.Join("|", h.Tts.Spoken)).IsEqualTo("One moment.|It's sunny.");

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("thanks");
        await VoiceHarness.WaitUntil(() => h.Tts.Spoken.Count == 3);
        await Assert.That(h.Tts.Spoken[2]).IsEqualTo("Anything else?");   // ask_user is silent: no "one moment"
    }


    [Test]
    public async Task Acknowledgement_AudioBlip_HasNoSpeech_LessWordy_AsksForBrevity()
    {
        await using var blip = new VoiceHarness();
        blip.Chat.Reply(Text("Plenty of words."));
        await blip.Start(Opts(ack: AiAcknowledgement.AudioBlip));
        await blip.Voice.PushToTalk();
        await blip.WaitForState(VoiceState.Listening);
        blip.Stt.Final("hi");
        await VoiceHarness.WaitUntil(() => blip.Sounds.Played.Contains(AiAction.Respond));
        await blip.WaitForState(VoiceState.Off);
        await Assert.That(blip.Tts.Spoken.Count).IsEqualTo(0);

        await using var brief = new VoiceHarness();
        brief.Chat.Reply(Text("Short."));
        await brief.Start(Opts(ack: AiAcknowledgement.LessWordy));
        await brief.Voice.PushToTalk();
        await brief.WaitForState(VoiceState.Listening);
        brief.Stt.Final("hi");
        await VoiceHarness.WaitUntil(() => brief.Tts.Spoken.Count == 1);
        await Assert.That(brief.Chat.Requests[0].Messages[0].Text.Contains("Be concise")).IsTrue();

        await using var none = new VoiceHarness();
        none.Chat.Reply(Text("Quiet."));
        await none.Start(Opts(ack: AiAcknowledgement.None));
        await none.Voice.PushToTalk();
        await none.WaitForState(VoiceState.Listening);
        none.Stt.Final("hi");
        await VoiceHarness.WaitUntil(() => none.Chat.Requests.Count == 1);
        await none.WaitForState(VoiceState.Off);
        await Assert.That(none.Sounds.Played.Count).IsEqualTo(0);
        await Assert.That(none.Tts.Spoken.Count).IsEqualTo(0);
    }


    [Test]
    public async Task StreamingEngine_SynthesizesAhead_AndPlaysThroughThePlayer()
    {
        await using var h = new VoiceHarness(withPlayer: true);
        h.Tts.CanSynthesizeToStream = true;
        h.Chat.Reply(Text("One. Two. Three."));
        await h.Start(Opts());

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("count");

        await VoiceHarness.WaitUntil(() => h.Player!.Played.Count == 3);
        await Assert.That(String.Join("|", h.Player!.Played)).IsEqualTo("One.|Two.|Three.");
        await Assert.That(h.Tts.Spoken.Count).IsEqualTo(0);
        await Assert.That(h.Tts.Synthesized.Count).IsEqualTo(3);
    }


    [Test]
    public async Task Pause_ReleasesTheMic_Resume_PicksUpAgain()
    {
        await using var h = new VoiceHarness();
        await h.Start(Opts(wakeWord: "hey"));
        await h.WaitForState(VoiceState.WaitingForWakeWord);

        await h.Voice.Pause();
        await Assert.That(h.Voice.State).IsEqualTo(VoiceState.Paused);
        await Assert.That(h.Stt.IsListening).IsFalse();

        await h.Voice.Resume();
        await h.WaitForState(VoiceState.WaitingForWakeWord);
        await Assert.That(h.Stt.IsListening).IsTrue();
    }


    [Test]
    public async Task RecognitionError_IsReported_AndTheLoopRecovers()
    {
        await using var h = new VoiceHarness();
        h.Chat.Reply(Text("Recovered."));
        await h.Start(Opts());
        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);

        h.Stt.Fail("network down");
        await h.WaitForState(VoiceState.Off);
        await Assert.That(h.VoiceEvents.OfType<VoiceError>().Any()).IsTrue();

        await h.Voice.PushToTalk();
        await h.WaitForState(VoiceState.Listening);
        h.Stt.Final("again");
        await VoiceHarness.WaitUntil(() => h.Tts.Spoken.Count == 1);
    }


    [Test]
    public async Task EnginesThatCompleteOnNativeCallbacks_AreNeverReentered()   // the iOS AVAudioPlayerNode.Stop() trap
    {
        var chat = new FakeChatClient().Reply(Text("One. Two. Three. Four."));
        var stt = new FakeSpeechToText();
        var tts = new CallbackTextToSpeech();
        var manager = new AiSessionManager(new InMemoryAiSessionStore(), new FakeChatClientProvider(chat), [], TimeProvider.System, memoryOptions: new AiMemoryOptions { Enabled = false });
        await using var session = await manager.Start();
        await using var voice = new VoiceConversation(stt, tts, new FakeSounds(), TimeProvider.System);

        await voice.Attach(session, new VoiceOptions());
        await voice.PushToTalk();
        await VoiceHarness.WaitUntil(() => voice.State == VoiceState.Listening);
        stt.Final("count to four");

        await VoiceHarness.WaitUntil(() => { lock (tts.Spoken) return tts.Spoken.Count == 4; });
        await VoiceHarness.WaitUntil(() => voice.State == VoiceState.Off);
        await Assert.That(String.Join(",", tts.ReentrantCalls)).IsEqualTo("");
    }


    [Test]
    public async Task Attach_IsOneSessionAtATime_AndRequestAccess_PassesThrough()   // P11
    {
        await using var h = new VoiceHarness();
        await h.Start(Opts());

        await using var other = await h.Manager.Start();
        await Assert.That(() => h.Voice.Attach(other)).Throws<InvalidOperationException>();

        h.Stt.Access = AccessState.Denied;
        await Assert.That(await h.Voice.RequestAccess()).IsEqualTo(AccessState.Denied);
    }


    [Test]
    public async Task VoiceSelectionTools_ListSampleAndChangeTheVoice()
    {
        var stt = new FakeSpeechToText();
        var tts = new FakeTextToSpeech();
        var voice = new VoiceConversation(stt, tts, new FakeSounds(), TimeProvider.System);
        var interceptor = new VoiceSelectionInterceptor(tts, voice);

        var h = TestHelpers.CreateHarness();
        await using var session = await h.Manager.Start();
        var ctx = new SessionLoadedContext(session, true);
        await interceptor.OnSessionLoaded(ctx);
        var tools = ctx.Tools.ToDictionary(x => x.Tool.Name, x => (AIFunction)x.Tool);

        async Task<string> Run(string name, params (string, object?)[] args)
        {
            var a = new AIFunctionArguments();
            foreach (var (k, v) in args) a[k] = v;
            return (await tools[name].InvokeAsync(a))!.ToString()!;
        }

        await Assert.That((await Run(VoiceSelectionInterceptor.ListToolName, ("culture", "en-GB"))).Contains("Liam")).IsTrue();
        await Run(VoiceSelectionInterceptor.SampleToolName, ("voiceId", "v1"));
        await Assert.That(tts.Options.Single()!.Voice!.Name).IsEqualTo("Ava");

        await Assert.That(await Run(VoiceSelectionInterceptor.ChangeToolName, ("voiceId", "v2"))).IsEqualTo("Voice changed to Liam.");
        await Assert.That(voice.TextToSpeech!.Voice!.Id).IsEqualTo("v2");
        await Assert.That((await Run(VoiceSelectionInterceptor.ChangeToolName, ("voiceId", "nope"))).StartsWith("Error")).IsTrue();
    }


    [Test]
    public async Task Registration_DoesNotRegisterSpeechEngines()   // D16
    {
        var services = new ServiceCollection();
        services.AddShinyAiConversationVoice(v => v.AddVoiceSelectionTools());

        await Assert.That(services.Any(x => x.ServiceType == typeof(IVoiceConversation))).IsTrue();
        await Assert.That(services.Any(x => x.ServiceType == typeof(ISpeechToTextService))).IsFalse();
        await Assert.That(services.Any(x => x.ServiceType == typeof(ISessionInterceptor) && x.ImplementationType == typeof(VoiceSelectionInterceptor))).IsTrue();
    }
}


public class QuestionHeuristicTests
{
    [Test]
    public async Task TerminatorRuns_AndClosingMarkup()
    {
        await Assert.That(QuestionHeuristic.EndsWithQuestion("Really?!")).IsTrue();
        await Assert.That(QuestionHeuristic.EndsWithQuestion("Is it **done?**")).IsTrue();
        await Assert.That(QuestionHeuristic.EndsWithQuestion("Done.")).IsFalse();
        await Assert.That(QuestionHeuristic.LastQuestion("Sure. Want it hot?!")).IsEqualTo("Want it hot?!");
    }
}
