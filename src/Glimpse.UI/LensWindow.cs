using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Glimpse.Contracts;

namespace Glimpse.UI;

public sealed class LensWindow : Window
{
    private bool ready;
    private readonly PixelRect initial;
    public event Action<PixelRect>? RectangleChanged;
    public event Action? Moving;
    public event Action? MoveCompleted;
    public const int Edge = 8, Header = 24;
    public PixelRect CaptureRect
    {
        get
        {
            var bounds = Native.Bounds(this);
            return new(bounds.X + Edge, bounds.Y + Header, bounds.Width - 2 * Edge, bounds.Height - Header - Edge);
        }
    }
    public LensWindow(PixelRect rect)
    {
        initial = rect;
        Title = "Glimpse Lens";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        MinWidth = 100; MinHeight = 100;
        var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(25, 118, 111)) };
        grid.Children.Add(new TextBlock
        {
            Text = "GLIMPSE  ·  拖动上边框移动 / 边角缩放",
            Foreground = Brushes.White,
            FontSize = 10,
            Margin = new Thickness(8, 3, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        });
        Content = grid;
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(Native.Handle(this)).AddHook(Hook);
            Native.Exclude(this);
            Native.Position(this, new(initial.X - Edge, initial.Y - Header,
                initial.Width + 2 * Edge, initial.Height + Header + Edge));
            ready = true;
            UpdateRegion();
        };
        SizeChanged += (_, _) => Changed();
        LocationChanged += (_, _) => Changed();
    }
    public void Restore(PixelRect rect)
    {
        Native.Position(this, new(rect.X - Edge, rect.Y - Header, rect.Width + 2 * Edge, rect.Height + Header + Edge));
        UpdateRegion();
    }
    private void Changed()
    {
        if (!ready) return;
        UpdateRegion();
        RectangleChanged?.Invoke(CaptureRect);
    }
    private void UpdateRegion()
    {
        var b = Native.Bounds(this);
        nint outside = Native.CreateRectRgn(0, 0, b.Width, b.Height);
        nint inside = Native.CreateRectRgn(Edge, Header, b.Width - Edge, b.Height - Edge);
        Native.CombineRgn(outside, outside, inside, 4); // RGN_DIFF: real hole, across process boundaries.
        Native.DeleteObject(inside);
        if (Native.SetWindowRgn(Native.Handle(this), outside, true) == 0)
        {
            Native.DeleteObject(outside);
            throw new InvalidOperationException("无法创建鼠标穿透区域，观察已停止。");
        }
        // USER32 owns outside after a successful SetWindowRgn.
    }
    private nint Hook(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message == 0x0231) Moving?.Invoke(); // WM_ENTERSIZEMOVE
        if (message == 0x0232) MoveCompleted?.Invoke(); // WM_EXITSIZEMOVE
        if (message != 0x0084) return 0; // WM_NCHITTEST
        long packed = lparam.ToInt64();
        int x = (short)(packed & 0xffff), y = (short)((packed >> 16) & 0xffff);
        var b = Native.Bounds(this);
        x -= b.X; y -= b.Y;
        bool left = x < Edge, right = x >= b.Width - Edge, top = y < Edge, bottom = y >= b.Height - Edge;
        handled = true;
        if (top && left) return 13;
        if (top && right) return 14;
        if (bottom && left) return 16;
        if (bottom && right) return 17;
        if (left) return 10;
        if (right) return 11;
        if (top) return 12;
        if (bottom) return 15;
        return 2; // HTCAPTION: header strip, interior is absent from region.
    }
}
