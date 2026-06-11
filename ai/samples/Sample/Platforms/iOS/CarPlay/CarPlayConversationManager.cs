using CarPlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.AiConversation;

namespace Sample;

public class CarPlayConversationManager
{
    readonly CPInterfaceController interfaceController;
    IAiConversationService? aiService;
    CPListTemplate? rootTemplate;
    CancellationTokenSource? listenCts;
    string? heard;
    string? spoken;
    AiState status;
    bool isCleanedUp;

    public CarPlayConversationManager(CPInterfaceController interfaceController)
    {
        this.interfaceController = interfaceController;
    }

    public void Show()
    {
        var services = IPlatformApplication.Current?.Services;
        if (services == null)
        {
            this.SetPlaceholder("App is starting…");
            return;
        }

        this.aiService = services.GetService<IAiConversationService>();
        if (this.aiService == null)
        {
            this.SetPlaceholder("AI service unavailable.");
            return;
        }

        this.status = this.aiService.Status;
        this.aiService.StatusChanged += this.OnStatusChanged;
        this.aiService.SpeechOccurred += this.OnSpeechOccurred;

        this.rootTemplate = this.BuildTemplate();
        this.interfaceController.SetRootTemplate(this.rootTemplate, false, null);
    }

    void SetPlaceholder(string message)
    {
        var placeholder = new CPListItem(message, null);
        var section = new CPListSection([placeholder]);
        var template = new CPListTemplate("K.I.T.T.", [section]);
        this.interfaceController.SetRootTemplate(template, false, null);
    }

    CPListTemplate BuildTemplate()
    {
        var primaryText = this.status == AiState.Idle ? "Tap to talk" : "Stop";
        var primary = new CPListItem(primaryText, this.status.ToString())
        {
            Handler = (item, completion) =>
            {
                this.ToggleListening();
                completion();
            }
        };

        var items = new List<ICPListTemplateItem> { primary };

        if (!String.IsNullOrWhiteSpace(this.heard))
            items.Add(new CPListItem("YOU", this.heard));

        if (!String.IsNullOrWhiteSpace(this.spoken))
            items.Add(new CPListItem("K.I.T.T.", this.spoken));

        var section = new CPListSection(items.ToArray(), "Conversation", null);
        return new CPListTemplate("K.I.T.T.", [section]);
    }

    void ToggleListening()
    {
        if (this.aiService == null)
            return;

        if (this.aiService.Status == AiState.Idle)
        {
            this.listenCts?.Dispose();
            this.listenCts = new CancellationTokenSource();
            var ct = this.listenCts.Token;
            _ = Task.Run(async () =>
            {
                try { await this.aiService.ListenAndTalk(ct); }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
                catch (Exception ex)
                {
                    var logger = IPlatformApplication.Current?.Services.GetService<ILogger<CarPlayConversationManager>>();
                    logger?.LogError(ex, "CarPlay ListenAndTalk failed");
                }
            });
        }
        else
        {
            try { this.listenCts?.Cancel(); } catch { }
        }
    }

    void OnStatusChanged(AiState state)
    {
        this.status = state;
        if (state == AiState.Listening)
        {
            this.heard = null;
            this.spoken = null;
        }
        this.RefreshOnMain();
    }

    void OnSpeechOccurred(ConversationSpeech speech)
    {
        switch (speech.Source)
        {
            case ConversationSpeechSource.Heard: this.heard = speech.Text; break;
            case ConversationSpeechSource.Spoken: this.spoken = speech.Text; break;
        }
        this.RefreshOnMain();
    }

    void RefreshOnMain()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (this.isCleanedUp || this.rootTemplate == null)
                return;

            var rebuilt = this.BuildTemplate();
            this.rootTemplate.UpdateSections(rebuilt.Sections);
        });
    }

    public void Cleanup()
    {
        this.isCleanedUp = true;
        if (this.aiService != null)
        {
            this.aiService.StatusChanged -= this.OnStatusChanged;
            this.aiService.SpeechOccurred -= this.OnSpeechOccurred;
            this.aiService = null;
        }

        try { this.listenCts?.Cancel(); } catch { }
        this.listenCts?.Dispose();
        this.listenCts = null;
        this.rootTemplate = null;
    }
}
