using Shiny.AiConversation.Voice;
using Shiny.Maui.Controls;

namespace MauiSample.Features.Aura;

public partial class AuraPage : ShinyContentPage
{
    // K.I.T.T.'s voice box: three columns of lamp segments, the center one tallest, lighting from the middle
    // outward with the AI's voice. Columns are rebuilt to fit the available height; frames only recolor segments.
    const double SegmentHeight = 9;
    const double SegmentGap = 4;
    const double ColumnWidth = 64;
    const double SideColumnFraction = 0.7;      // side columns are shorter than the center one
    const double FullScaleLevel = 0.22;         // output RMS that lights a whole column
    const double Release = 0.80;                // per-tick fall-off: fast attack, slower decay

    static readonly Color LitCore = Color.FromArgb("#FFE0D0");
    static readonly Color Lit = Color.FromArgb("#FF1A00");
    static readonly Color Dim = Color.FromArgb("#2A0400");

    readonly List<BoxView>[] columns = [[], [], []];
    readonly VerticalStackLayout[] columnViews = new VerticalStackLayout[3];
    readonly Random rng = new();
    IDispatcherTimer? barTimer;
    double scannerTravel;
    double builtForHeight;
    double centerLevel, sideLevel;

    public AuraPage()
    {
        InitializeComponent();
        for (var i = 0; i < 3; i++)
        {
            this.columnViews[i] = new VerticalStackLayout { Spacing = SegmentGap, VerticalOptions = LayoutOptions.Center, WidthRequest = ColumnWidth };
            this.BarsHost.Children.Add(this.columnViews[i]);
        }
        this.BarsHost.SizeChanged += this.OnBarsHostSizeChanged;
        this.ScannerTopChannel.SizeChanged += this.OnScannerChannelSizeChanged;
    }

    void OnBarsHostSizeChanged(object? sender, EventArgs e)
    {
        var available = this.BarsHost.Height;
        if (available <= 0 || Math.Abs(available - this.builtForHeight) < 1)
            return;

        this.builtForHeight = available;
        var center = Math.Max(5, (int)((available + SegmentGap) / (SegmentHeight + SegmentGap)));
        if (center % 2 == 0)
            center--;   // odd, so there is a middle segment

        var side = Math.Max(3, (int)(center * SideColumnFraction));
        if (side % 2 == 0)
            side--;

        this.BuildColumn(0, side);
        this.BuildColumn(1, center);
        this.BuildColumn(2, side);
    }

    void BuildColumn(int index, int segments)
    {
        var column = this.columns[index];
        column.Clear();
        this.columnViews[index].Children.Clear();
        for (var i = 0; i < segments; i++)
        {
            var segment = new BoxView
            {
                Color = Dim,
                HeightRequest = SegmentHeight,
                WidthRequest = ColumnWidth,
                CornerRadius = 2
            };
            column.Add(segment);
            this.columnViews[index].Children.Add(segment);
        }
    }

    void OnScannerChannelSizeChanged(object? sender, EventArgs e)
    {
        // Travel = channel inner width minus scanner width, accounting for the channel's own padding.
        var inner = this.ScannerTopChannel.Width - 4; // matches Padding="2" on each channel border
        this.scannerTravel = Math.Max(0, inner - this.ScannerTop.Width);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.StartScanner();
        this.StartBarTicker();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        this.AbortAnimation("KittScanner");
        this.barTimer?.Stop();
        this.barTimer = null;
    }

    void StartBarTicker()
    {
        this.barTimer = this.Dispatcher.CreateTimer();
        this.barTimer.Interval = TimeSpan.FromMilliseconds(50);
        this.barTimer.Tick += (_, _) =>
        {
            if (this.BindingContext is AuraViewModel vm)
                this.UpdateBars(vm.AudioLevel, vm.CurrentState);
        };
        this.barTimer.Start();
    }

    void UpdateBars(double level, VoiceState state)
    {
        // the modulator is the AI's voice: it only moves while the AI is speaking
        double target = 0, sideTarget = 0;
        if (state == VoiceState.Speaking)
        {
            // gain + a compressive curve so ordinary speech swings most of the column, with a little flicker
            var normalized = Math.Clamp(level / FullScaleLevel, 0, 1);
            target = Math.Clamp(Math.Pow(normalized, 0.55) * (0.85 + this.rng.NextDouble() * 0.3), 0, 1);
            sideTarget = Math.Clamp(target * (0.75 + this.rng.NextDouble() * 0.35), 0, 1);
        }

        // jump up instantly, fall back gradually
        this.centerLevel = Math.Max(target, this.centerLevel * Release);
        this.sideLevel = Math.Max(sideTarget, this.sideLevel * Release);

        Light(this.columns[0], this.sideLevel);
        Light(this.columns[1], this.centerLevel);
        Light(this.columns[2], this.sideLevel);
    }

    /// <summary>
    /// Lights segments from the middle of the column outward, symmetrically, like the original lamps.
    /// </summary>
    static void Light(List<BoxView> column, double level)
    {
        var count = column.Count;
        if (count == 0)
            return;

        var middle = count / 2;
        var reach = (int)Math.Round(level * (middle + 1));   // segments lit on each side of the middle
        for (var i = 0; i < count; i++)
        {
            var distance = Math.Abs(i - middle);
            var color = distance < reach
                ? (distance == 0 && reach > 1 ? LitCore : Lit)
                : Dim;

            if (column[i].Color != color)
                column[i].Color = color;
        }
    }

    void StartScanner()
    {
        // Top scanner sweeps left→right; bottom mirrors right→left for the
        // signature Knight Rider counter-sweep look. Travel is measured live
        // so it adapts when the panel fills different screen sizes.
        var animation = new Animation();
        animation.Add(0.0, 0.5, new Animation(v => this.ScannerTop.TranslationX = v * this.scannerTravel, 0, 1, Easing.SinInOut));
        animation.Add(0.5, 1.0, new Animation(v => this.ScannerTop.TranslationX = v * this.scannerTravel, 1, 0, Easing.SinInOut));
        animation.Add(0.0, 0.5, new Animation(v => this.ScannerBottom.TranslationX = -v * this.scannerTravel, 0, 1, Easing.SinInOut));
        animation.Add(0.5, 1.0, new Animation(v => this.ScannerBottom.TranslationX = -v * this.scannerTravel, 1, 0, Easing.SinInOut));

        animation.Commit(this, "KittScanner", length: 2400, easing: Easing.Linear, repeat: () => true);
    }
}
