using System.ComponentModel;
using System.Runtime.InteropServices;
using Glimpse.Contracts;

namespace Glimpse.UI;

public static class ToolbarPlacement
{
    // All values are physical pixels in the virtual desktop coordinate system.
    // The input is the hollow capture area, so include the actual lens frame.
    public static PixelRect Place(PixelRect captureRect, int toolbarWidth, int toolbarHeight,
        PixelRect workArea, int gap = 8)
    {
        if (toolbarWidth <= 0 || toolbarHeight <= 0 || workArea.Width <= 0 || workArea.Height <= 0 || gap < 0)
            throw new ArgumentOutOfRangeException(nameof(toolbarWidth));
        int width = Math.Min(toolbarWidth, workArea.Width), height = Math.Min(toolbarHeight, workArea.Height);
        long left = (long)captureRect.X - LensWindow.Edge, top = (long)captureRect.Y - LensWindow.Header;
        long right = (long)captureRect.X + captureRect.Width + LensWindow.Edge;
        long bottom = (long)captureRect.Y + captureRect.Height + LensWindow.Edge;
        long workRight = (long)workArea.X + workArea.Width, workBottom = (long)workArea.Y + workArea.Height;
        PixelRect Clamped(long x, long y) => new((int)Math.Clamp(x, workArea.X, workRight - width),
            (int)Math.Clamp(y, workArea.Y, workBottom - height), width, height);
        if (right + gap + width <= workRight) return Clamped(right + gap, top);
        if (left - gap - width >= workArea.X) return Clamped(left - gap - width, top);
        if (bottom + gap + height <= workBottom) return Clamped(left, bottom + gap);
        if (top - gap - height >= workArea.Y) return Clamped(left, top - gap - height);
        // A lens covering nearly the whole display leaves no outside space. Keep
        // controls reachable in the work area rather than putting them off-screen.
        return Clamped(right + gap, top);
    }

    internal static PixelRect WorkArea(PixelRect captureRect)
    {
        var bounds = new Rect { Left = captureRect.X, Top = captureRect.Y,
            Right = checked(captureRect.X + captureRect.Width), Bottom = checked(captureRect.Y + captureRect.Height) };
        nint monitor = MonitorFromRect(ref bounds, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
}
