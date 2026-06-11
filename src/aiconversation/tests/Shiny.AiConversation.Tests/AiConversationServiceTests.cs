using Imposter.Abstractions;
using Microsoft.Extensions.AI;
using Shiny.AiConversation.Infrastructure;
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
            .Store(Arg<string?>.Any(), Arg<ChatResponse>.Any(), Arg<CancellationToken>.Any())
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
            .Store(Arg<string?>.Any(), Arg<ChatResponse>.Any(), Arg<CancellationToken>.Any())
            .Called(Count.Once());
    }

    [Test]
    public async Task TalkTo_DoesNotCallMessageStore_WhenNotConfigured()
    {
        var (service, _, chatClient, _, _, _, messageStore, _) = CreateService(withMessageStore: false);
        SetupResponse(chatClient, "No store");

        await service.TalkTo("Test", CancellationToken.None);

        messageStore
            .Store(Arg<string?>.Any(), Arg<ChatResponse>.Any(), Arg<CancellationToken>.Any())
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

    #region Helpers

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
