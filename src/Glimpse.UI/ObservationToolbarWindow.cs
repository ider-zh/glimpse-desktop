using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glimpse.Contracts;

namespace Glimpse.UI;

// Capture controls have their own small surface; the sidebar is reserved for
// instructions and answers. All placement inputs/outputs are physical pixels.
public sealed class ObservationToolbarWindow : Window
{
    public event Action? RecordsRequested, ToggleObservationRequested, ReselectRequested,
        IgnoreRegionRequested, AnalyzeDynamicRegionsRequested, ClearIgnoreRegionsRequested, CloseLensRequested;
    private readonly TextBlock state = new() { FontSize = 11, Foreground = Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock detail = new() { FontSize = 11, Foreground = Brushes.SlateGray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button observe, clear;
    private readonly StackPanel commands = new() { Orientation = Orientation.Horizontal };
    private PixelRect? captureRect;
    private bool ready, ownerClosing, followQueued, closed, analyzing;
    private int ignoreCount;
    public bool IsCaptureExcluded { get; private set; }
    public ObservationToolbarWindow()
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = "Glimpse 观察工具栏"; Width = 400; Height = 91; MinWidth = 0; MinHeight = 0;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var rows = new StackPanel();
        commands.Children.Add(observe = CreateIconButton("ObserveButton", "\uE768", "开始观察", () => ToggleObservationRequested?.Invoke()));
        commands.Children.Add(CreateIconButton("ReselectButton", "\uE7C4", "重新框选观察区", () => ReselectRequested?.Invoke()));
        commands.Children.Add(CreateIconButton("IgnoreRegionButton", "\uE8A7", "框选忽略区域：跳过视频，只识别外部文字", () => IgnoreRegionRequested?.Invoke()));
        commands.Children.Add(CreateIconButton("AnalyzeDynamicRegionsButton", "\uE721", "自动识别动态区域：本地分析约 2 秒，完成后可手动清除", () => AnalyzeDynamicRegionsRequested?.Invoke()));
        commands.Children.Add(clear = CreateIconButton("ClearIgnoreRegionsButton", "\uE74D", "清除所有忽略区域", () => ClearIgnoreRegionsRequested?.Invoke()));
        commands.Children.Add(CreateIconButton("RecordsButton", "\uE8A5", "打开记录区", () => RecordsRequested?.Invoke()));
        commands.Children.Add(CreateIconButton("CloseLensButton", "\uE8BB", "关闭观察框并停止采集，保留已记录文字", () => CloseLensRequested?.Invoke()));
        commands.Children.Add(state); rows.Children.Add(commands);
        detail.Margin = new Thickness(5, 5, 5, 0); rows.Children.Add(detail);
        Content = new Border { Margin = new Thickness(4), Padding = new Thickness(8), Background = new SolidColorBrush(Color.FromRgb(250, 252, 253)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(202, 219, 217)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = rows };
        SourceInitialized += (_, _) =>
        {
            Native.Exclude(this); IsCaptureExcluded = ready = true;
            HwndSource.FromHwnd(Native.Handle(this)).AddHook(Hook); QueueFollow();
        };
        SizeChanged += (_, _) => QueueFollow();
        Closing += (_, e) => { if (!ownerClosing) { e.Cancel = true; Dispatcher.BeginInvoke(() => CloseLensRequested?.Invoke()); } };
        Closed += (_, _) => { closed = true; ready = false; };
        UpdateState(false, "等待开始观察", 0);
    }
    private Button CreateIconButton(string name, string glyph, string tooltip, Action action)
    {
        var button = new Button { Name = name, Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
            Width = 37, Height = 32, MinHeight = 32, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0), ToolTip = tooltip };
        AutomationProperties.SetName(button, tooltip); ToolTipService.SetInitialShowDelay(button, 350);
        button.Click += (_, _) => action(); RegisterName(name, button); return button;
    }
    public void UpdateState(bool observing, string status, int ignoreCount)
    {
        this.ignoreCount = ignoreCount;
        state.Text = observing ? "观察中" : "已暂停";
        state.Foreground = observing ? new SolidColorBrush(Color.FromRgb(25, 118, 111)) : Brushes.SlateGray;
        observe.Content = observing ? "\uE769" : "\uE768";
        string label = observing ? "暂停观察，停止截图和识别" : "开始观察";
        observe.ToolTip = label; AutomationProperties.SetName(observe, label);
        UpdateEnabled();
        detail.Text = analyzing ? "正在本地分析动态区域，请暂时不要滚动…" : ignoreCount > 0 ? $"已忽略 {ignoreCount} 个区域 · {status}" : status;
        detail.ToolTip = detail.Text;
        AutomationProperties.SetLiveSetting(detail, AutomationLiveSetting.Polite);
    }
    public void SetAnalyzing(bool value)
    {
        analyzing = value; UpdateEnabled();
        if (value) detail.Text = "正在本地分析动态区域，请暂时不要滚动…";
    }
    private void UpdateEnabled()
    {
        foreach (var button in commands.Children.OfType<Button>())
            button.IsEnabled = !analyzing || button.Name is "CloseLensButton" or "RecordsButton";
        clear.IsEnabled = !analyzing && ignoreCount > 0;
    }
    public void Follow(PixelRect rect) { captureRect = rect; QueueFollow(); }
    private void QueueFollow()
    {
        if (!ready || followQueued || ownerClosing) return;
        followQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            followQueued = false;
            if (!ready || ownerClosing || captureRect is not { } rect) return;
            uint dpi = ToolbarPlacement.GetDpiForWindow(Native.Handle(this));
            double scale = dpi > 0 ? dpi / 96d : VisualTreeHelper.GetDpi(this).DpiScaleX;
            var placed = ToolbarPlacement.PlaceObservation(rect, (int)Math.Ceiling(400 * scale), (int)Math.Ceiling(91 * scale), ToolbarPlacement.WorkArea(rect));
            if (Native.Bounds(this) != placed) Native.Position(this, placed);
        });
    }
    private nint Hook(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    { if (message is 0x02E0 or 0x007E or 0x001A) QueueFollow(); return 0; }
    public void CloseForOwner() { if (closed) return; ownerClosing = true; ready = false; Close(); }
}
