using System.Diagnostics;
using Shiny.AiConversation.Voice;
using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// A push-to-talk <see cref="ChatInputAction"/> over <see cref="IVoiceConversation"/>. The heard utterance and the
/// reply flow back into the chat as bubbles. The voice conversation is attached to the chat's session on first use.
/// Invoking it while the AI is speaking stops the reply and listens instead.
/// </summary>
public class AiMicrophoneInputAction : ChatInputAction
{
    public AiMicrophoneInputAction() => this.Text = this.ListenText;

    /// <summary>Action sheet label.</summary>
    public string ListenText { get; set; } = "🎤 Voice Input";

    /// <summary>The voice conversation; resolved from the MAUI service provider when null.</summary>
    public IVoiceConversation? Voice { get; set; }

    /// <summary>The session to talk to (the chat view sets this).</summary>
    public IAiSession? Session { get; set; }

    /// <summary>Options used when this action attaches the voice conversation.</summary>
    public VoiceOptions? VoiceOptions { get; set; }

    public override async Task InvokeAsync(ChatView chatView)
    {
        await base.InvokeAsync(chatView).ConfigureAwait(true);

        var voice = this.Voice ?? Application.Current?.Handler?.MauiContext?.Services.GetService<IVoiceConversation>();
        if (voice is null || this.Session is null)
            return;

        try
        {
            var access = await voice.RequestAccess().ConfigureAwait(true);
            if (access != AccessState.Available)
                return;

            if (!ReferenceEquals(voice.Session, this.Session))
            {
                // one session holds the microphone at a time: hand it to this chat
                await voice.Detach().ConfigureAwait(true);
                await voice.Attach(this.Session, this.VoiceOptions).ConfigureAwait(true);
            }
            await voice.PushToTalk().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AiMicrophoneInputAction: {ex.Message}");
        }
    }
}
