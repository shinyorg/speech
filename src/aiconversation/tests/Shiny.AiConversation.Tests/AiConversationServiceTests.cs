using Imposter.Abstractions;
using Microsoft.Extensions.AI;
using Shiny.AiConversation.Infrastructure;
using Shiny.Audio;
using Shiny.Speech;

namespace Shiny.AiConversation.Tests;

public class AiConversationServiceTests
{
    static readonly DateTimeOffset FixedTime = new(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);

    static (AiConversationService Service,
        IChatClientProviderImposter ChatClientProvider,
        IChatClientImposter ChatClient,
        ISpeechToTextServiceImposter SpeechToText,
        ITextToSpeechServiceImposter TextToSpeech,
        IAudioPlayerImposter AudioPlayer,
        IMessageStoreImposter MessageStore,
        FakeTimeProvider TimeProvider) CreateService(bool withMessageStore = true, IContextProvider[]? contextProviders = null)
    {
        var chatClientProvider = IChatClientProvider.Imposter();
        var chatClient = IChatClient.Imposter();
        var speechToText = ISpeechToTextService.Imposter();
        var textToSpeech = ITextToSpeechService.Imposter();
        var audioPlayer = IAudioPlayer.Imposter();
        var messageStore = IMessageStore.Imposter();
        var soundProvider = ISoundProvider.Imposter();
        var timeProvider = new FakeTimeProvider(FixedTime);

        chatClientProvider
            .GetChatClient(Arg<CancellationToken>.Any())
            .ReturnsAsync(chatClient.Instance());

        messageStore
            .Store(Arg<string?>.Any(), Arg<string?>.Any(), Arg<ChatResponse>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.CompletedTask);

        textToSpeech
            .SpeakAsync(Arg<string>.Any(), Arg<Shiny.Speech.TextToSpeechOptions?>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.CompletedTask);

        // The new Speech 2.0 contract: Start opens a session, Stop closes it. Both return Task.
        speechToText
            .Start(Arg<SpeechRecognitionOptions?>.Any())
            .Returns(Task.CompletedTask);

        speechToText
            .Stop()
            .Returns(Task.CompletedTask);

        soundProvider
            .Play(Arg<AiAction>.Any())
            .Returns(Task.CompletedTask);

        contextProviders ??= [new ContextProvider(timeProvider, [])];

        var service = new AiConversationService(
            chatClientProvider.Instance(),
            speechToText.Instance(),
            textToSpeech.Instance(),
            null,
            soundProvider.Instance(),
            contextProviders,
            withMessageStore ? messageStore.Instance() : null
        );
        service.QuietWords = null; // disable interruption in tests by default

        return (service, chatClientProvider, chatClient, speechToText, textToSpeech, audioPlayer, messageStore, timeProvider);
    }

    #region TalkTo

    [Test]
    public async Task TalkTo_AddsUserMessageToCurrentChat()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, "Hello back!");

        await service.TalkTo("Hello", CancellationToken.None);

        await Assert.That(service.CurrentChatMessages.Count).IsEqualTo(2);
        await Assert.That(service.CurrentChatMessages[0].Role).IsEqualTo(ChatRole.User);
        await Assert.That(service.CurrentChatMessages[0].Text).IsEqualTo("Hello");
    }

    [Test]
    public async Task TalkTo_AddsAssistantResponseToCurrentChat()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, "I am AI");

        await service.TalkTo("Hi", CancellationToken.None);

        await Assert.That(service.CurrentChatMessages.Count).IsEqualTo(2);
        await Assert.That(service.CurrentChatMessages[1].Role).IsEqualTo(ChatRole.Assistant);
        await Assert.That(service.CurrentChatMessages[1].Text).IsEqualTo("I am AI");
    }

    [Test]
    public async Task TalkTo_RaisesAiRespondedEvent()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, "Response text");

        AiResponse? received = null;
        service.AiResponded += r => received = r;

        await service.TalkTo("Test", CancellationToken.None);

        // AiResponded fires via Task.Run, give it a moment
        await Task.Delay(100);

        await Assert.That(received).IsNotNull();
        await Assert.That(received!.Response.Text).IsEqualTo("Response text");
    }

    [Test]
    public async Task TalkTo_StoresUserAndAiMessages_WhenMessageStoreConfigured()
    {
        var (service, _, chatClient, _, _, _, messageStore, _) = CreateService();
        SetupResponse(chatClient, "Stored response");

        await service.TalkTo("Stored input", CancellationToken.None);

        messageStore
            .Store(Arg<string?>.Any(), Arg<string?>.Any(), Arg<ChatResponse>.Any(), Arg<CancellationToken>.Any())
            .Called(Count.Once());
    }

    [Test]
    public async Task TalkTo_DoesNotCallMessageStore_WhenNotConfigured()
    {
        var (service, _, chatClient, _, _, _, messageStore, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, "No store");

        await service.TalkTo("Test", CancellationToken.None);

        messageStore
            .Store(Arg<string?>.Any(), Arg<string?>.Any(), Arg<ChatResponse>.Any(), Arg<CancellationToken>.Any())
            .Called(Count.Never());
    }

    [Test]
    public async Task TalkTo_TransitionsThroughCorrectStates()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, "OK");

        var states = new System.Collections.Concurrent.ConcurrentBag<AiState>();
        service.StatusChanged += state => states.Add(state);

        await service.TalkTo("Test", CancellationToken.None);

        // StatusChanged fires via Task.Run, wait for all 3 states to arrive
        for (var i = 0; i < 100 && states.Count < 3; i++)
            await Task.Delay(50);

        await Assert.That(states).Contains(AiState.Thinking);
        await Assert.That(states).Contains(AiState.Responding);
        await Assert.That(states).Contains(AiState.Idle);
    }

    [Test]
    public async Task TalkTo_SpeaksResponse_WhenAcknowledgementIsFull()
    {
        var (service, _, chatClient, _, textToSpeech, _, _, _) = CreateService(withMessageStore: false);
        service.Acknowledgement = AiAcknowledgement.Full;
        SetupResponse(chatClient, "Speak this");

        await service.TalkTo("Test", CancellationToken.None);

        textToSpeech
            .SpeakAsync(Arg<string>.Any(), Arg<Shiny.Speech.TextToSpeechOptions?>.Any(), Arg<CancellationToken>.Any())
            .Called(Count.AtLeast(1));
    }

    [Test]
    public async Task TalkTo_DoesNotSpeak_WhenAcknowledgementIsNone()
    {
        var (service, _, chatClient, _, textToSpeech, _, _, _) = CreateService(withMessageStore: false);
        service.Acknowledgement = AiAcknowledgement.None;
        SetupResponse(chatClient, "Silent");

        await service.TalkTo("Test", CancellationToken.None);

        textToSpeech
            .SpeakAsync(Arg<string>.Any(), Arg<Shiny.Speech.TextToSpeechOptions?>.Any(), Arg<CancellationToken>.Any())
            .Called(Count.Never());
    }

    #endregion

    #region System Prompts

    [Test]
    public async Task TalkTo_IncludesSystemPromptsFromContextProvider()
    {
        var contextProvider = IContextProvider.Imposter();
        contextProvider
            .Apply(Arg<AiContext>.Any())
            .Returns(ctx =>
            {
                ctx.SystemPrompts.Add("You are a test bot.");
                return Task.CompletedTask;
            });

        var (service, _, chatClient, _, _, _, _, _) = CreateService(
            withMessageStore: false,
            contextProviders: [contextProvider.Instance()]
        );

        IEnumerable<ChatMessage>? capturedMessages = null;
        chatClient
            .GetResponseAsync(
                Arg<IEnumerable<ChatMessage>>.Any(),
                Arg<ChatOptions?>.Any(),
                Arg<CancellationToken>.Any()
            )
            .Returns((messages, _, _) =>
            {
                capturedMessages = messages;
                return Task.FromResult(CreateResponse("OK"));
            });

        await service.TalkTo("Hello", CancellationToken.None);

        await Assert.That(capturedMessages).IsNotNull();
        var msgList = capturedMessages!.ToList();
        var systemMessages = msgList.Where(m => m.Role == ChatRole.System).ToList();
        await Assert.That(systemMessages.Any(m => m.Text == "You are a test bot.")).IsTrue();
    }

    [Test]
    public async Task TalkTo_AddsLessWordyPrompt_WhenAcknowledgementIsLessWordy()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        service.Acknowledgement = AiAcknowledgement.LessWordy;

        IEnumerable<ChatMessage>? capturedMessages = null;
        chatClient
            .GetResponseAsync(
                Arg<IEnumerable<ChatMessage>>.Any(),
                Arg<ChatOptions?>.Any(),
                Arg<CancellationToken>.Any()
            )
            .Returns((messages, _, _) =>
            {
                capturedMessages = messages;
                return Task.FromResult(CreateResponse("OK"));
            });

        await service.TalkTo("Hello", CancellationToken.None);

        var msgList = capturedMessages!.ToList();
        var systemMessages = msgList.Where(m => m.Role == ChatRole.System).ToList();
        await Assert.That(systemMessages.Any(m => m.Text!.Contains("concise"))).IsTrue();
    }

    #endregion

    #region ClearCurrentChat

    [Test]
    public async Task ClearCurrentChat_RemovesAllMessages()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, "Response");

        await service.TalkTo("Hello", CancellationToken.None);
        await Assert.That(service.CurrentChatMessages.Count).IsEqualTo(2);

        service.ClearCurrentChat();
        await Assert.That(service.CurrentChatMessages.Count).IsEqualTo(0);
    }

    #endregion

    #region GetChatHistory / ClearChatHistory

    [Test]
    public async Task GetChatHistory_ThrowsWhenNoMessageStore()
    {
        var (service, _, _, _, _, _, _, _) = CreateService(withMessageStore: false);

        await Assert.That(() => service.GetChatHistory()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ClearChatHistory_ThrowsWhenNoMessageStore()
    {
        var (service, _, _, _, _, _, _, _) = CreateService(withMessageStore: false);

        await Assert.That(() => service.ClearChatHistory()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task GetChatHistory_DelegatesToMessageStore()
    {
        var (service, _, _, _, _, _, messageStore, _) = CreateService();
        var expected = new List<AiChatMessage>
        {
            new("1", "Hello", FixedTime, ChatMessageDirection.User)
        };

        messageStore
            .Query(
                Arg<string?>.Any(),
                Arg<DateTimeOffset?>.Any(),
                Arg<DateTimeOffset?>.Any(),
                Arg<int?>.Any(),
                Arg<CancellationToken>.Any()
            )
            .ReturnsAsync((IReadOnlyList<AiChatMessage>)expected.AsReadOnly());

        var result = await service.GetChatHistory(limit: 10);
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Message).IsEqualTo("Hello");
    }

    [Test]
    public async Task ClearChatHistory_DelegatesToMessageStore()
    {
        var (service, _, _, _, _, _, messageStore, _) = CreateService();
        messageStore.Clear(Arg<DateTimeOffset?>.Any()).Returns(Task.CompletedTask);

        await service.ClearChatHistory();

        messageStore.Clear(Arg<DateTimeOffset?>.Any()).Called(Count.Once());
    }

    #endregion

    #region ListenAndTalk

    [Test]
    public async Task ListenAndTalk_ThrowsWhenWakeWordActive()
    {
        var (service, _, _, speechToText, _, _, _, _) = CreateService(withMessageStore: false);

        await service.StartWakeWord("Hey Test");

        await Assert.That(() => service.ListenAndTalk(CancellationToken.None))
            .Throws<InvalidOperationException>();

        await service.StopWakeWord();
    }

    #endregion

    #region Wake Word

    [Test]
    public async Task StartWakeWord_SetsWakeWordAndState()
    {
        var (service, _, _, speechToText, _, _, _, _) = CreateService(withMessageStore: false);

        await service.StartWakeWord("Hey Bot");

        await Assert.That(service.IsWakeWordEnabled).IsTrue();
        await Assert.That(service.WakeWord).IsEqualTo("Hey Bot");

        await service.StopWakeWord();
        await Assert.That(service.WakeWord).IsNull();
    }

    [Test]
    public async Task StartWakeWord_ThrowsIfAlreadyActive()
    {
        var (service, _, _, speechToText, _, _, _, _) = CreateService(withMessageStore: false);

        await service.StartWakeWord("Hey Bot");

        await Assert.That(() => service.StartWakeWord("Hey Bot"))
            .Throws<InvalidOperationException>();

        await service.StopWakeWord();
    }

    [Test]
    public async Task StartWakeWord_StartsSpeechSession()
    {
        var (service, _, _, speechToText, _, _, _, _) = CreateService(withMessageStore: false);

        await service.StartWakeWord("Hey Bot");

        speechToText
            .Start(Arg<SpeechRecognitionOptions?>.Any())
            .Called(Count.Once());

        await service.StopWakeWord();

        speechToText
            .Stop()
            .Called(Count.AtLeast(1));
    }

    #endregion

    #region Structured Turns

    const string TurnWithQuestions =
        """
        {
          "reply": "Sure - which time works for you?",
          "questions": [
            {
              "id": "time",
              "text": "Which time?",
              "choices": [
                { "id": "am", "label": "9:00 am" },
                { "id": "pm", "label": "2:00 pm" }
              ],
              "allowMultiple": false
            }
          ]
        }
        """;

    const string TurnWithoutQuestions = """{"reply":"Booked for 9am.","questions":[]}""";

    [Test]
    public async Task StructuredTurn_SurfacesReplyNotRawJson()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, TurnWithQuestions);

        AiResponse? received = null;
        service.AiResponded += r => received = r;

        await service.TalkTo("Book me in", CancellationToken.None);
        await Task.Delay(100);

        await Assert.That(received).IsNotNull();
        await Assert.That(received!.Text).IsEqualTo("Sure - which time works for you?");
        await Assert.That(received.Turn).IsNotNull();
    }

    [Test]
    public async Task StructuredTurn_PopulatesPendingQuestionsAndExpectsResponse()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, TurnWithQuestions);

        AiResponse? received = null;
        service.AiResponded += r => received = r;

        await service.TalkTo("Book me in", CancellationToken.None);
        await Task.Delay(100);

        await Assert.That(received!.ExpectsResponse).IsTrue();
        await Assert.That(service.PendingQuestions.Count).IsEqualTo(1);
        await Assert.That(service.PendingQuestions[0].Id).IsEqualTo("time");
        await Assert.That(service.PendingQuestions[0].Choices!.Length).IsEqualTo(2);
        await Assert.That(service.PendingQuestions[0].Choices![0].Label).IsEqualTo("9:00 am");
    }

    [Test]
    public async Task StructuredTurn_WithNoQuestions_DoesNotExpectResponse()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, TurnWithoutQuestions);

        AiResponse? received = null;
        service.AiResponded += r => received = r;

        await service.TalkTo("9am please", CancellationToken.None);
        await Task.Delay(100);

        await Assert.That(received!.ExpectsResponse).IsFalse();
        await Assert.That(service.PendingQuestions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StructuredTurn_ReplacesPendingQueueEachTurn()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);

        SetupResponse(chatClient, TurnWithQuestions);
        await service.TalkTo("Book me in", CancellationToken.None);
        await Assert.That(service.PendingQuestions.Count).IsEqualTo(1);

        // The model stops asking, so the queue empties rather than holding the earlier question.
        SetupResponse(chatClient, TurnWithoutQuestions);
        await service.TalkTo("9am", CancellationToken.None);
        await Assert.That(service.PendingQuestions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StructuredTurn_ParsesJsonWrappedInMarkdownFence()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, $"Here you go:\n```json\n{TurnWithoutQuestions}\n```");

        AiResponse? received = null;
        service.AiResponded += r => received = r;

        await service.TalkTo("Book it", CancellationToken.None);
        await Task.Delay(100);

        await Assert.That(received!.Text).IsEqualTo("Booked for 9am.");
    }

    [Test]
    public async Task StructuredTurn_SpeaksReplyNotRawJson()
    {
        var (service, _, chatClient, _, textToSpeech, _, _, _) = CreateService(withMessageStore: false);
        service.Acknowledgement = AiAcknowledgement.Full;
        SetupResponse(chatClient, TurnWithoutQuestions);

        string? spoken = null;
        textToSpeech
            .SpeakAsync(Arg<string>.Any(), Arg<Shiny.Speech.TextToSpeechOptions?>.Any(), Arg<CancellationToken>.Any())
            .Returns((text, _, _) =>
            {
                spoken = text;
                return Task.CompletedTask;
            });

        await service.TalkTo("Book it", CancellationToken.None);

        await Assert.That(spoken).IsEqualTo("Booked for 9am.");
    }

    [Test]
    public async Task StructuredTurn_StoresReplyNotRawJson()
    {
        var (service, _, chatClient, _, _, _, messageStore, _) = CreateService();
        SetupResponse(chatClient, TurnWithoutQuestions);

        string? stored = null;
        messageStore
            .Store(Arg<string?>.Any(), Arg<string?>.Any(), Arg<ChatResponse>.Any(), Arg<CancellationToken>.Any())
            .Returns((_, assistantMessage, _, _) =>
            {
                stored = assistantMessage;
                return Task.CompletedTask;
            });

        await service.TalkTo("Book it", CancellationToken.None);

        await Assert.That(stored).IsEqualTo("Booked for 9am.");
    }

    [Test]
    public async Task StructuredTurn_KeepsRawEnvelopeInChatHistory()
    {
        // The JSON stays in the transcript on purpose - it anchors the model to the format next turn.
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, TurnWithoutQuestions);

        await service.TalkTo("Book it", CancellationToken.None);

        await Assert.That(service.CurrentChatMessages[1].Text).IsEqualTo(TurnWithoutQuestions);
    }

    [Test]
    public async Task UnparseableResponse_FallsBackToPlainTextAndQuestionHeuristic()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, "Which time works for you?");

        AiResponse? received = null;
        service.AiResponded += r => received = r;

        await service.TalkTo("Book me in", CancellationToken.None);
        await Task.Delay(100);

        await Assert.That(received!.Turn).IsNull();
        await Assert.That(received.Text).IsEqualTo("Which time works for you?");
        await Assert.That(received.ExpectsResponse).IsTrue();
        await Assert.That(service.PendingQuestions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StructuredOutputMode_None_SkipsParsingEntirely()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        service.StructuredOutputMode = AiStructuredOutputMode.None;
        SetupResponse(chatClient, TurnWithQuestions);

        AiResponse? received = null;
        service.AiResponded += r => received = r;

        await service.TalkTo("Book me in", CancellationToken.None);
        await Task.Delay(100);

        await Assert.That(received!.Turn).IsNull();
        await Assert.That(received.Text).IsEqualTo(TurnWithQuestions);
        await Assert.That(service.PendingQuestions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StructuredOutputMode_None_OmitsTurnContractPrompt()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        service.StructuredOutputMode = AiStructuredOutputMode.None;

        IEnumerable<ChatMessage>? captured = null;
        chatClient
            .GetResponseAsync(
                Arg<IEnumerable<ChatMessage>>.Any(),
                Arg<ChatOptions?>.Any(),
                Arg<CancellationToken>.Any()
            )
            .Returns((messages, _, _) =>
            {
                captured = messages;
                return Task.FromResult(CreateResponse("OK"));
            });

        await service.TalkTo("Hello", CancellationToken.None);

        var prompts = captured!.Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? "");
        await Assert.That(prompts.Any(p => p.Contains("'questions'"))).IsFalse();
    }

    [Test]
    public async Task TurnContractPrompt_LimitsToOneQuestion_WhenSpokenAloud()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        service.Acknowledgement = AiAcknowledgement.Full;

        IEnumerable<ChatMessage>? captured = null;
        chatClient
            .GetResponseAsync(
                Arg<IEnumerable<ChatMessage>>.Any(),
                Arg<ChatOptions?>.Any(),
                Arg<CancellationToken>.Any()
            )
            .Returns((messages, _, _) =>
            {
                captured = messages;
                return Task.FromResult(CreateResponse(TurnWithoutQuestions));
            });

        await service.TalkTo("Hello", CancellationToken.None);

        var prompts = captured!.Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? "").ToList();
        await Assert.That(prompts.Any(p => p.Contains("at most one question per turn"))).IsTrue();
    }

    [Test]
    public async Task TurnContractPrompt_AllowsSeveralQuestions_WhenNotSpokenAloud()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        service.Acknowledgement = AiAcknowledgement.None;

        IEnumerable<ChatMessage>? captured = null;
        chatClient
            .GetResponseAsync(
                Arg<IEnumerable<ChatMessage>>.Any(),
                Arg<ChatOptions?>.Any(),
                Arg<CancellationToken>.Any()
            )
            .Returns((messages, _, _) =>
            {
                captured = messages;
                return Task.FromResult(CreateResponse(TurnWithoutQuestions));
            });

        await service.TalkTo("Hello", CancellationToken.None);

        var prompts = captured!.Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? "").ToList();
        await Assert.That(prompts.Any(p => p.Contains("more than one question"))).IsTrue();
    }

    [Test]
    public async Task ClearCurrentChat_ClearsPendingQuestions()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, TurnWithQuestions);

        await service.TalkTo("Book me in", CancellationToken.None);
        await Assert.That(service.PendingQuestions.Count).IsEqualTo(1);

        service.ClearCurrentChat();
        await Assert.That(service.PendingQuestions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MultipleQuestions_AreAllQueued()
    {
        var (service, _, chatClient, _, _, _, _, _) = CreateService(withMessageStore: false);
        SetupResponse(
            chatClient,
            """
            {
              "reply": "A couple of things first.",
              "questions": [
                { "id": "time", "text": "Which time?" },
                { "id": "size", "text": "How many people?", "allowMultiple": true }
              ]
            }
            """
        );

        await service.TalkTo("Book me in", CancellationToken.None);

        await Assert.That(service.PendingQuestions.Count).IsEqualTo(2);
        await Assert.That(service.PendingQuestions[1].AllowMultiple).IsTrue();
        await Assert.That(service.PendingQuestions[1].HasChoices).IsFalse();
    }

    #endregion

    #region Follow-up window

    [Test]
    public async Task FollowUp_ContinuesWithoutWakeWord_WhenAiAsksAQuestion()
    {
        var (service, _, chatClient, speechToText, _, _, _, _) = CreateService(withMessageStore: false);
        service.Acknowledgement = AiAcknowledgement.AudioBlip; // no TTS, so no echo-suppression window
        service.FollowUpTimeout = TimeSpan.FromSeconds(5);

        var calls = 0;
        chatClient
            .GetResponseAsync(
                Arg<IEnumerable<ChatMessage>>.Any(),
                Arg<ChatOptions?>.Any(),
                Arg<CancellationToken>.Any()
            )
            .Returns((_, _, _) =>
            {
                var turn = Interlocked.Increment(ref calls) == 1 ? TurnWithQuestions : TurnWithoutQuestions;
                return Task.FromResult(CreateResponse(turn));
            });

        await service.StartWakeWord("Hey Bot");
        try
        {
            await RaiseKeywordAsync(speechToText, "Hey Bot");
            await RaiseFinalAsync(speechToText, "Book me in");
            await WaitForAsync(() => service.PendingQuestions.Count == 1);

            // The answer arrives with no wake word - the follow-up window is still open.
            await RaiseFinalAsync(speechToText, "9am");
            await WaitForAsync(() => Volatile.Read(ref calls) == 2);

            await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
            await Assert.That(service.PendingQuestions.Count).IsEqualTo(0);
        }
        finally
        {
            await service.StopWakeWord();
        }
    }

    [Test]
    public async Task FollowUp_TimesOutAndRequiresWakeWordAgain_WhenNobodyAnswers()
    {
        var (service, _, chatClient, speechToText, _, _, _, _) = CreateService(withMessageStore: false);
        service.Acknowledgement = AiAcknowledgement.AudioBlip;
        service.FollowUpTimeout = TimeSpan.FromMilliseconds(300);

        var calls = 0;
        chatClient
            .GetResponseAsync(
                Arg<IEnumerable<ChatMessage>>.Any(),
                Arg<ChatOptions?>.Any(),
                Arg<CancellationToken>.Any()
            )
            .Returns((_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(CreateResponse(TurnWithQuestions));
            });

        await service.StartWakeWord("Hey Bot");
        try
        {
            await RaiseKeywordAsync(speechToText, "Hey Bot");
            await RaiseFinalAsync(speechToText, "Book me in");
            await WaitForAsync(() => service.PendingQuestions.Count == 1);

            // Nobody answers - the window closes and the queue is dropped.
            await WaitForAsync(() => service.PendingQuestions.Count == 0, timeoutMs: 3000);
            await Assert.That(service.PendingQuestions.Count).IsEqualTo(0);

            // Unrelated speech in the room must NOT reach the AI now that the window has closed.
            await RaiseFinalAsync(speechToText, "did you watch the game last night");
            await Task.Delay(400);

            await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
        }
        finally
        {
            await service.StopWakeWord();
        }
    }

    #endregion

    #region Helpers

    // Both readers flush anything buffered before they start waiting (so a stale keyword or a partial
    // from the previous turn can't be mistaken for this one). That means a raise landing before the
    // loop reaches its wait is discarded - hence the beat before each one.
    static async Task RaiseKeywordAsync(ISpeechToTextServiceImposter speechToText, string keyword)
    {
        await Task.Delay(150);
        speechToText.KeywordHeard.Raise(speechToText, keyword);
    }

    static async Task RaiseFinalAsync(ISpeechToTextServiceImposter speechToText, string text)
    {
        await Task.Delay(150);
        speechToText.ResultReceived.Raise(speechToText, new SpeechRecognitionResult(text, true, 0.9f));
    }

    static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition())
            await Task.Delay(25);
    }


    static void SetupResponse(IChatClientImposter chatClient, string responseText)
    {
        chatClient
            .GetResponseAsync(
                Arg<IEnumerable<ChatMessage>>.Any(),
                Arg<ChatOptions?>.Any(),
                Arg<CancellationToken>.Any()
            )
            .ReturnsAsync(CreateResponse(responseText));
    }

    static ChatResponse CreateResponse(string text)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        response.FinishReason = ChatFinishReason.Stop;
        return response;
    }

    #endregion
}

internal class FakeTimeProvider(DateTimeOffset fixedTime) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => fixedTime;
}
