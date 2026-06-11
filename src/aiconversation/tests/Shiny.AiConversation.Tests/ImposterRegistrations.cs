using Imposter.Abstractions;
using Microsoft.Extensions.AI;
using Shiny.AiConversation;
using Shiny.Speech;

[assembly: GenerateImposter(typeof(IChatClientProvider))]
[assembly: GenerateImposter(typeof(IMessageStore))]
[assembly: GenerateImposter(typeof(ISpeechToTextService))]
[assembly: GenerateImposter(typeof(ITextToSpeechService))]
[assembly: GenerateImposter(typeof(IAudioPlayer))]
[assembly: GenerateImposter(typeof(IChatClient))]
[assembly: GenerateImposter(typeof(IContextProvider))]
[assembly: GenerateImposter(typeof(ISoundProvider))]
