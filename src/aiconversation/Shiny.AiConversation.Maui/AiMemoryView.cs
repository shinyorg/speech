using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.AiConversation.Maui;

/// <summary>
/// Shows everything the assistant remembers, grouped by scope, and lets the user edit, delete or forget
/// everything. Resolves <see cref="IAiSessionManager.Memory"/> from the app's services unless <see cref="Memory"/> is set.
/// </summary>
public class AiMemoryView : ContentView
{
    readonly ObservableCollection<AiMemoryDocument> items = [];
    readonly CollectionView list;
    readonly Label empty;
    readonly Editor editor;
    readonly Label editorTitle;
    readonly VerticalStackLayout editPanel;
    AiMemoryDocument? editing;


    public AiMemoryView()
    {
        this.list = new CollectionView
        {
            ItemsSource = this.items,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var path = new Label { FontAttributes = FontAttributes.Bold };
                path.SetBinding(Label.TextProperty, static (AiMemoryDocument d) => d.Path);

                var meta = new Label { FontSize = 12, Opacity = 0.7 };
                meta.SetBinding(Label.TextProperty, static (AiMemoryDocument d) => d.Scope);

                var preview = new Label { FontSize = 13, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
                preview.SetBinding(Label.TextProperty, static (AiMemoryDocument d) => d.Content);

                return new VerticalStackLayout { Padding = new Thickness(12, 8), Spacing = 2, Children = { path, meta, preview } };
            })
        };
        this.list.SelectionChanged += (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is AiMemoryDocument doc)
                this.Edit(doc);
        };

        this.empty = new Label { Text = "The assistant hasn't saved anything yet.", HorizontalOptions = LayoutOptions.Center, Margin = 24, IsVisible = false };

        this.editorTitle = new Label { FontAttributes = FontAttributes.Bold };
        this.editor = new Editor { AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 120 };
        var save = new Button { Text = "Save" };
        save.Clicked += async (_, _) => await this.SaveEdit();
        var delete = new Button { Text = "Delete" };
        delete.Clicked += async (_, _) => await this.DeleteEdit();
        var cancel = new Button { Text = "Cancel" };
        cancel.Clicked += (_, _) => this.CloseEditor();
        this.editPanel = new VerticalStackLayout
        {
            Padding = 12,
            Spacing = 8,
            IsVisible = false,
            Children = { this.editorTitle, this.editor, new HorizontalStackLayout { Spacing = 8, Children = { save, delete, cancel } } }
        };

        var forget = new Button { Text = "Forget everything", Margin = 12 };
        forget.Clicked += async (_, _) => await this.ForgetEverything();

        var grid = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)]
        };
        grid.Add(this.editPanel, 0, 0);
        grid.Add(this.list, 0, 1);
        grid.Add(this.empty, 0, 1);
        grid.Add(forget, 0, 2);
        this.Content = grid;
    }


    public static readonly BindableProperty MemoryProperty = BindableProperty.Create(
        nameof(Memory), typeof(IAiMemoryService), typeof(AiMemoryView), null,
        propertyChanged: async (b, _, _) => await ((AiMemoryView)b).Reload()
    );

    public IAiMemoryService? Memory
    {
        get => (IAiMemoryService?)GetValue(MemoryProperty);
        set => SetValue(MemoryProperty, value);
    }

    public static readonly BindableProperty ConfirmForgetProperty = BindableProperty.Create(nameof(ConfirmForget), typeof(bool), typeof(AiMemoryView), true);

    /// <summary>Asks before "Forget everything".</summary>
    public bool ConfirmForget
    {
        get => (bool)GetValue(ConfirmForgetProperty);
        set => SetValue(ConfirmForgetProperty, value);
    }


    IAiMemoryService? Service
        => this.Memory
           ?? (this.Handler?.MauiContext?.Services ?? Application.Current?.Handler?.MauiContext?.Services)?.GetService<IAiSessionManager>()?.Memory;


    protected override async void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (this.Handler != null)
            await this.Reload();
    }


    /// <summary>Reloads the list.</summary>
    public async Task Reload()
    {
        if (this.Service is not { } memory)
            return;

        var docs = await memory.List();
        this.items.Clear();
        foreach (var doc in docs.OrderBy(x => x.Scope).ThenBy(x => x.Path))
            this.items.Add(doc);

        this.empty.IsVisible = this.items.Count == 0;
    }


    void Edit(AiMemoryDocument doc)
    {
        this.editing = doc;
        this.editorTitle.Text = $"{doc.Path} ({doc.Scope.ToString().ToLowerInvariant()})";
        this.editor.Text = doc.Content;
        this.editPanel.IsVisible = true;
    }


    void CloseEditor()
    {
        this.editing = null;
        this.editPanel.IsVisible = false;
        this.list.SelectedItem = null;
    }


    async Task SaveEdit()
    {
        if (this.editing is not { } doc || this.Service is not { } memory)
            return;

        await memory.Save(doc.Scope, doc.ScopeKey, doc.Path, this.editor.Text ?? String.Empty, doc.Description);
        this.CloseEditor();
        await this.Reload();
    }


    async Task DeleteEdit()
    {
        if (this.editing is not { } doc || this.Service is not { } memory)
            return;

        await memory.Delete(doc.Scope, doc.ScopeKey, doc.Path);
        this.CloseEditor();
        await this.Reload();
    }


    async Task ForgetEverything()
    {
        if (this.Service is not { } memory)
            return;

        if (this.ConfirmForget && this.Window?.Page is { } page)
        {
            var ok = await page.DisplayAlertAsync("Forget everything?", "The assistant will lose every note it has saved about you.", "Forget", "Cancel");
            if (!ok)
                return;
        }

        await memory.Clear();
        this.CloseEditor();
        await this.Reload();
    }
}
