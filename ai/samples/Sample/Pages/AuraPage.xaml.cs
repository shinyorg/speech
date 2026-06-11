using Shiny.AiConversation;
using Shiny.Maui.Controls.FloatingPanel;

namespace Sample.Pages;

public partial class AuraPage : ShinyContentPage
{
    // Classic K.I.T.T. voice modulator: a tall center beam flanked by two equal,
    // smaller bouncing beams.
    const int BarCount = 3;
    const double CenterBarHeightFraction = 0.95;
    const double SideBarHeightFraction = 0.55;
    const double BarWidth = 84;

    readonly BoxView[] bars = new BoxView[BarCount];
    readonly double[] barMaxHeights = new double[BarCount];
    readonly Random rng = new();
    IDispatcherTimer? barTimer;
    double scannerTravel;

    public AuraPage()
    {
        InitializeComponent();
        this.BuildBars();
        this.BarsHost.SizeChanged += this.OnBarsHostSizeChanged;
        this.ScannerTopChannel.SizeChanged += this.OnScannerChannelSizeChanged;
    }

    void BuildBars()
    {
        for (var i = 0; i < BarCount; i++)
        {
            var bar = new BoxView
            {
                Color = Color.FromArgb("#FF1A00"),
                WidthRequest = BarWidth,
                HeightRequest = 6,
                CornerRadius = 3,
                VerticalOptions = LayoutOptions.Center,
                Shadow = new Shadow
                {
                    Brush = new SolidColorBrush(Color.FromArgb("#FF3A00")),
                    Offset = new Point(0, 0),
                    Radius = 16,
                    Opacity = 0.9f
                }
            };
            this.bars[i] = bar;
            this.BarsHost.Children.Add(bar);
        }
    }

    void OnBarsHostSizeChanged(object? sender, EventArgs e)
    {
        var available = this.BarsHost.Height;
        if (available <= 0)
            return;

        this.barMaxHeights[0] = available * SideBarHeightFraction;
        this.barMaxHeights[1] = available * CenterBarHeightFraction;
        this.barMaxHeights[2] = available * SideBarHeightFraction;
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
        this.barTimer.Interval = TimeSpan.FromMilliseconds(70);
        this.barTimer.Tick += (_, _) =>
        {
            if (this.BindingContext is AuraViewModel vm)
                this.UpdateBars(vm.AudioLevel, vm.CurrentState);
        };
        this.barTimer.Start();
    }

    void UpdateBars(double level, AiState state)
    {
        var effectiveLevel = state switch
        {
            AiState.Responding => Math.Max(level, 0.10),
            AiState.Listening  => 0.22 + (this.rng.NextDouble() * 0.18),
            AiState.Thinking   => 0.14 + (this.rng.NextDouble() * 0.12),
            _                  => 0.0
        };

        // Side beams "bounce" equally — share a single jitter draw so they stay symmetric.
        var sideJitter = 0.7 + (this.rng.NextDouble() * 0.3);
        var centerJitter = 0.8 + (this.rng.NextDouble() * 0.2);

        this.bars[0].HeightRequest = Math.Max(6, this.barMaxHeights[0] * effectiveLevel * sideJitter);
        this.bars[1].HeightRequest = Math.Max(6, this.barMaxHeights[1] * effectiveLevel * centerJitter);
        this.bars[2].HeightRequest = Math.Max(6, this.barMaxHeights[2] * effectiveLevel * sideJitter);
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
