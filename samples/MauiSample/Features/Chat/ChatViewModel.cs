using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MauiSample.Services;
using Shiny;
using Shiny.AiConversation;

namespace MauiSample.Features.Chat;

/// <summary>
/// Hands <see cref="Shiny.AiConversation.Maui.AiChatView"/> the app's shared session (the same one the voice page
/// uses), so typed and spoken turns land in one conversation.
/// </summary>
[ShellMap<ChatPage>("chat")]
public partial class ChatViewModel(ConversationHost host) : ObservableObject, IPageLifecycleAware
{
    [ObservableProperty]
    IAiSession? session;

    [ObservableProperty]
    string title = "Chat";

    public async void OnAppearing()
    {
        host.SessionChanged += this.OnSessionChanged;
        this.Show(await host.GetSession());
    }

    public void OnDisappearing() => host.SessionChanged -= this.OnSessionChanged;

    [RelayCommand]
    async Task NewConversation() => this.Show(await host.NewConversation());

    void OnSessionChanged(object? sender, IAiSession s) => MainThread.BeginInvokeOnMainThread(() => this.Show(s));

    void Show(IAiSession s)
    {
        this.Session = s;
        this.Title = s.Title ?? "Chat";
    }
}
