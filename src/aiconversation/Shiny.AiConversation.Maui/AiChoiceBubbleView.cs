using Microsoft.Maui.Layouts;
using Shiny.Maui.Controls.Chat;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// The bubble body used for AI replies that carry <see cref="AiQuestion.Choices"/> - the reply text
/// followed by a button per choice. A custom message template replaces the whole bubble content, so
/// this renders the text itself rather than only appending the buttons.
/// </summary>
/// <remarks>
/// Tapping a choice sends its <see cref="AiChoice.Label"/> back as the user's answer. Nothing is matched
/// locally beyond that - the model already has the question in context and resolves the answer itself.
/// </remarks>
public class AiChoiceBubbleView : ContentView
{
    readonly AiChatView chatView;
    readonly AiChoiceTemplateSelector selector;
    readonly VerticalStackLayout root;
    readonly Label textLabel;

    readonly HashSet<string> selectedLabels = new(StringComparer.Ordinal);
    readonly List<Button> multiSendButtons = [];

    internal AiChoiceBubbleView(AiChatView chatView, AiChoiceTemplateSelector selector)
    {
        this.chatView = chatView;
        this.selector = selector;

        this.textLabel = new Label { LineBreakMode = LineBreakMode.WordWrap };
        this.root = new VerticalStackLayout { Spacing = 8, Children = { this.textLabel } };
        this.Content = this.root;
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        this.selectedLabels.Clear();
        this.multiSendButtons.Clear();

        if (this.BindingContext is not ChatMessage message)
            return;

        this.textLabel.Text = message.Body ?? String.Empty;
        this.textLabel.TextColor = this.chatView.OtherTextColor;
        this.textLabel.FontSize = this.chatView.BubbleFontSize;
        this.textLabel.FontFamily = this.chatView.BubbleFontFamily;

        // Everything after the text label is rebuilt per message - views are recycled by the underlying
        // collection view, so a stale choice set would otherwise stick to the next bubble.
        while (this.root.Children.Count > 1)
            this.root.Children.RemoveAt(this.root.Children.Count - 1);

        var questions = AiChoiceTemplateSelector.ReadQuestions(message);
        if (questions is null)
            return;

        var answered = this.selector.IsAnswered(message.MessageId);
        foreach (var question in questions.Where(x => x.HasChoices))
            this.root.Children.Add(this.BuildQuestion(message, question, answered));
    }

    View BuildQuestion(ChatMessage message, AiQuestion question, bool answered)
    {
        var container = new VerticalStackLayout { Spacing = 6 };
        var chips = new FlexLayout
        {
            Wrap = FlexWrap.Wrap,
            Direction = FlexDirection.Row,
            JustifyContent = FlexJustify.Start
        };

        foreach (var choice in question.Choices!)
        {
            var button = this.BuildChip(choice.Label);
            button.IsEnabled = !answered;

            if (question.AllowMultiple)
                button.Clicked += (_, _) => this.ToggleChoice(button, choice.Label);
            else
                button.Clicked += async (_, _) => await this.SendAsync(message, choice.Label).ConfigureAwait(true);

            chips.Children.Add(button);
        }

        container.Children.Add(chips);

        if (question.AllowMultiple)
        {
            // Multi-select needs an explicit commit - there is no other way to know the user is done picking.
            var send = this.BuildChip(this.chatView.ChoiceSendText);
            send.IsEnabled = false; // enabled once something is picked
            send.Clicked += async (_, _) =>
            {
                if (this.selectedLabels.Count > 0)
                    await this.SendAsync(message, String.Join(", ", this.selectedLabels)).ConfigureAwait(true);
            };

            container.Children.Add(send);
            this.multiSendButtons.Add(send);
        }

        return container;
    }

    Button BuildChip(string text) => new()
    {
        Text = text,
        FontSize = Math.Max(12, this.chatView.BubbleFontSize - 1),
        Padding = new Thickness(12, 4),
        Margin = new Thickness(0, 0, 6, 6),
        MinimumHeightRequest = 0,
        HeightRequest = 34,
        CornerRadius = 17,
        BorderWidth = 1,
        BorderColor = this.chatView.OtherTextColor,
        TextColor = this.chatView.OtherTextColor,
        BackgroundColor = Colors.Transparent
    };

    void ToggleChoice(Button button, string label)
    {
        if (!this.selectedLabels.Remove(label))
            this.selectedLabels.Add(label);

        var isSelected = this.selectedLabels.Contains(label);
        button.BackgroundColor = isSelected ? this.chatView.OtherTextColor : Colors.Transparent;
        button.TextColor = isSelected ? this.chatView.OtherBubbleColor : this.chatView.OtherTextColor;

        foreach (var send in this.multiSendButtons)
            send.IsEnabled = this.selectedLabels.Count > 0;
    }

    async Task SendAsync(ChatMessage message, string answer)
    {
        // One answer per question set - re-tapping a stale bubble would confuse the conversation.
        this.selector.MarkAnswered(message.MessageId);
        this.SetChoicesEnabled(false);

        await this.selector.SendAnswerAsync(answer).ConfigureAwait(true);
    }

    void SetChoicesEnabled(bool enabled)
    {
        for (var i = 1; i < this.root.Children.Count; i++)
        {
            if (this.root.Children[i] is VerticalStackLayout group)
                SetEnabledRecursive(group, enabled);
        }

        static void SetEnabledRecursive(Layout layout, bool enabled)
        {
            foreach (var child in layout.Children)
            {
                switch (child)
                {
                    case Button button:
                        button.IsEnabled = enabled;
                        break;

                    case Layout nested:
                        SetEnabledRecursive(nested, enabled);
                        break;
                }
            }
        }
    }
}
