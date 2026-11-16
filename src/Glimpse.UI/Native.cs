using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Glimpse.Contracts;

namespace Glimpse.UI;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    internal static nint Handle(Window window) => new WindowInteropHelper(window).Handle;
    internal static void Exclude(Window window)
    {
        if (!SetWindowDisplayAffinity(Handle(window), 0x11) ||
            !GetWindowDisplayAffinity(Handle(window), out uint value) || value != 0x11)
            throw new InvalidOperationException("Windows 无法排除 Glimpse 窗口，观察已禁用。需要 Windows 10 2004 或更高版本和桌面合成。");
    }
    internal static PixelRect Bounds(Window window)
    {
        if (!GetWindowRect(Handle(window), out var r)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
    internal static void Position(Window window, PixelRect rect) =>
        SetWindowPos(Handle(window), 0, rect.X, rect.Y, rect.Width, rect.Height, 0x14);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(nint window, out uint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] internal static extern int CombineRgn(nint dest, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] internal static extern int SetWindowRgn(nint window, nint region, bool redraw);
}
