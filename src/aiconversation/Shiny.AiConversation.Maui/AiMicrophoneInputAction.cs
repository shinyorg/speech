using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// A push-to-talk <see cref="ChatInputAction"/> that hands the microphone to
/// <see cref="IAiConversationService.ListenAndTalk"/>. The heard utterance and the AI reply both flow
/// back into the chat as bubbles, so nothing is written into the entry field. Invoking it while a
/// listen is already running cancels that listen.
/// </summary>
public class AiMicrophoneInputAction : ChatInputAction
{
    CancellationTokenSource? cts;

    public AiMicrophoneInputAction() => this.Text = ListenText;

    /// <summary>Action sheet label shown when idle.</summary>
    public string ListenText { get; set; } = "🎤 Voice Input";

    /// <summary>Action sheet label shown while a listen is in progress.</summary>
    public string CancelText { get; set; } = "Stop Listening";

    /// <summary>The conversation service to talk to. Resolved from the MAUI service provider when null.</summary>
    public IAiConversationService? AiService { get; set; }

    public override async Task InvokeAsync(ChatView chatView)
    {
        await base.InvokeAsync(chatView).ConfigureAwait(true);

        if (this.cts is { } active)
        {
            // second tap while listening - cancel the active turn
            await active.CancelAsync().ConfigureAwait(true);
            return;
        }

        var ai = this.AiService ?? Application.Current?.Handler?.MauiContext?.Services.GetService<IAiConversationService>();
        if (ai is null)
            return;

        var access = await ai.RequestAccess().ConfigureAwait(true);
        if (access != AccessState.Available)
            return;

        this.cts = new CancellationTokenSource();
        this.Text = this.CancelText;
        try
        {
            await ai.ListenAndTalk(this.cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // wake word running, mic unavailable, etc - the service surfaces fatal errors on
            // ErrorOccurred, which the chat session renders as a bubble
            Debug.WriteLine($"AiMicrophoneInputAction: {ex.Message}");
        }
        finally
        {
            this.cts.Dispose();
            this.cts = null;
            this.Text = this.ListenText;
        }
    }
}
