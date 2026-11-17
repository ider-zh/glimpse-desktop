using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Glimpse.Contracts;

namespace Glimpse.UI;

public sealed class SelectionWindow : Window
{
    private Native.Point start;
    private bool selecting;
    private readonly Canvas canvas = new();
    private readonly Rectangle selection = new() { Stroke = Brushes.Turquoise, StrokeThickness = 2, Fill = Brushes.Transparent };
    public PixelRect? Selected { get; private set; }
    public SelectionWindow(PixelRect? limit = null)
    {
        Title = limit is null ? "选择观察区域" : "选择忽略区域"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(90, 0, 12, 24));
        Topmost = true; ShowInTaskbar = false; Cursor = Cursors.Cross;
        Content = canvas;
        canvas.Children.Add(new TextBlock
        {
            Text = limit is null ? "拖动选择观察区域 · Esc 取消" : "框选视频等动态区域 · Esc 取消",
            Foreground = Brushes.White,
            FontSize = limit is null ? 24 : 14,
            Margin = new Thickness(limit is null ? 40 : 12)
        });
        canvas.Children.Add(selection);
        SourceInitialized += (_, _) =>
        {
            Native.Exclude(this);
            Native.Position(this, limit ?? new(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77),
                Native.GetSystemMetrics(78), Native.GetSystemMetrics(79)));
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
        MouseLeftButtonDown += (_, _) => { Native.GetCursorPos(out start); selecting = true; CaptureMouse(); };
        MouseMove += (_, _) =>
        {
            if (!selecting) return;
            Native.GetCursorPos(out var current);
            var a = PointFromScreen(new(start.X, start.Y));
            var b = PointFromScreen(new(current.X, current.Y));
            Canvas.SetLeft(selection, Math.Min(a.X, b.X)); Canvas.SetTop(selection, Math.Min(a.Y, b.Y));
            selection.Width = Math.Abs(a.X - b.X); selection.Height = Math.Abs(a.Y - b.Y);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (!selecting) return;
            ReleaseMouseCapture(); Native.GetCursorPos(out var end);
            int left = Math.Min(start.X, end.X), top = Math.Min(start.Y, end.Y);
            int right = Math.Max(start.X, end.X), bottom = Math.Max(start.Y, end.Y);
            if (limit is { } bounds)
            {
                left = Math.Clamp(left, bounds.X, bounds.X + bounds.Width);
                right = Math.Clamp(right, bounds.X, bounds.X + bounds.Width);
                top = Math.Clamp(top, bounds.Y, bounds.Y + bounds.Height);
                bottom = Math.Clamp(bottom, bounds.Y, bounds.Y + bounds.Height);
            }
            var rect = new PixelRect(left, top, right - left, bottom - top);
            if (!rect.IsValid) { selecting = false; return; }
            Selected = rect; DialogResult = true;
        };
    }
}
