using CommunityToolkit.Mvvm.ComponentModel;
using Shiny;
using Shiny.AiConversation;
using Shiny.Maui.Controls.Chat;

namespace MauiSample.Features.Chat;

/// <summary>
/// The new <see cref="ChatView"/> is provider-driven: instead of binding a message collection and
/// wiring send/load-more commands, the control is handed an <see cref="IChatSessionProvider"/> plus a
/// <c>SessionId</c> and drives everything itself. The provider here is <c>new</c>'d up over
/// <see cref="IAiConversationService"/> — see <see cref="AiConversationChatSessionProvider"/>.
/// </summary>
[ShellMap<ChatPage>("chat")]
public partial class ChatViewModel(IAiConversationService aiService) : ObservableObject
{
    public IChatSessionProvider Provider { get; } = new AiConversationChatSessionProvider(aiService);
    public string SessionId => AiConversationChatSessionProvider.DefaultSessionId;
}
