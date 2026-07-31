using CommunityToolkit.Mvvm.ComponentModel;
using Shiny;
using Shiny.AiConversation;
using Shiny.AiConversation.Maui;

namespace MauiSample.Features.Chat;

/// <summary>
/// Nothing to wire up: <see cref="AiChatView"/> resolves <see cref="IAiConversationService"/> from the
/// app's service provider, builds its own chat session provider, and backfills history from the
/// registered message store. This view model exists only to carry the Shell route.
/// </summary>
[ShellMap<ChatPage>("chat")]
public partial class ChatViewModel : ObservableObject;
