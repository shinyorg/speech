using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.AiConversation;
using Shiny.Maui.Controls.Chat;

namespace Sample.Pages;

public partial class ChatViewModel(IAiConversationService aiService, IDialogs dialogs)
    : ObservableObject, IPageLifecycleAware
{
    const int PageSize = 25;
    bool hasMoreHistory = true;
    bool isLoadingHistory;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public List<ChatParticipant> Participants { get; } =
    [
        new()
        {
            Id = "copilot",
            DisplayName = "Copilot",
            Avatar = new FontImageSource
            {
                Glyph = "\U0001F916",
                FontFamily = "OpenSansRegular",
                Size = 24,
                Color = Colors.White
            }
        }
    ];

    public async void OnAppearing()
    {
        aiService.AiResponded += this.OnAiResponded;

        if (this.Messages.Count == 0)
            await this.LoadHistory();
    }

    public void OnDisappearing()
    {
        aiService.AiResponded -= this.OnAiResponded;
    }

    void OnAiResponded(AiResponse response)
    {
        if (response.Response.Text is { } text)
        {
            var usage = response.Response.Usage;
            var bubble = AppendTokenFooter(
                text,
                usage?.InputTokenCount,
                usage?.OutputTokenCount,
                usage?.TotalTokenCount
            );

            MainThread.BeginInvokeOnMainThread(() =>
            {
                this.Messages.Add(new ChatMessage
                {
                    Text = bubble,
                    IsFromMe = false,
                    SenderId = "copilot",
                    Timestamp = DateTimeOffset.Now,
                    DateSent = DateTimeOffset.Now
                });
            });
        }
    }

    async Task LoadHistory()
    {
        if (this.isLoadingHistory)
            return;

        this.isLoadingHistory = true;
        try
        {
            var history = await aiService.GetChatHistory(limit: PageSize).ConfigureAwait(false);
            this.hasMoreHistory = history.Count >= PageSize;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var msg in history)
                    this.Messages.Add(ToChatMessage(msg));
            });
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Error", ex.Message);
        }
        finally
        {
            this.isLoadingHistory = false;
        }
    }

    [RelayCommand]
    async Task LoadMore()
    {
        if (!this.hasMoreHistory || this.Messages.Count == 0)
            return;

        try
        {
            var oldest = this.Messages[0].Timestamp;
            var older = await aiService.GetChatHistory(endDate: oldest.AddTicks(-1), limit: PageSize);
            this.hasMoreHistory = older.Count >= PageSize;

            for (var i = older.Count - 1; i >= 0; i--)
                this.Messages.Insert(0, ToChatMessage(older[i]));
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Error", ex.Message);
        }
    }

    [RelayCommand]
    async Task Send(string? text)
    {
        if (String.IsNullOrWhiteSpace(text))
            return;

        this.Messages.Add(new ChatMessage
        {
            Text = text,
            IsFromMe = true,
            SenderId = "me",
            Timestamp = DateTimeOffset.Now,
            DateSent = DateTimeOffset.Now
        });

        try
        {
            await aiService.TalkTo(text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await dialogs.Alert("Error", ex.Message);
        }
    }

    static ChatMessage ToChatMessage(AiChatMessage msg)
    {
        var text = msg.Direction == ChatMessageDirection.AI
            ? AppendTokenFooter(msg.Message, msg.InputTokens, msg.OutputTokens, msg.TotalTokens)
            : msg.Message;

        return new ChatMessage
        {
            Text = text,
            IsFromMe = msg.Direction == ChatMessageDirection.User,
            SenderId = msg.Direction == ChatMessageDirection.User ? "me" : "copilot",
            Timestamp = msg.Timestamp,
            DateSent = msg.Timestamp
        };
    }

    static string AppendTokenFooter(string body, long? input, long? output, long? total)
    {
        var footer = FormatTokenFooter(input, output, total);
        return footer is null ? body : body + "\n\n— " + footer;
    }

    internal static string? FormatTokenFooter(long? input, long? output, long? total)
    {
        if (total is null && input is null && output is null)
            return null;

        var ci = CultureInfo.InvariantCulture;
        var totalStr = (total ?? ((input ?? 0) + (output ?? 0))).ToString("N0", ci);

        if (input.HasValue && output.HasValue)
            return $"{totalStr} tokens ({input.Value.ToString("N0", ci)} in · {output.Value.ToString("N0", ci)} out)";

        return $"{totalStr} tokens";
    }
}
