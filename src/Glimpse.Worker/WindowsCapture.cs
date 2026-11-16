using System.ComponentModel;
using System.Runtime.InteropServices;
using Glimpse.Contracts;
using Glimpse.Core;

namespace Glimpse.Worker;

public sealed class WindowsCapture : IScreenCapture
{
    public byte[] Capture(PixelRect rect)
    {
        if (!rect.IsValid) throw new ArgumentOutOfRangeException(nameof(rect));
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
        if (rect.X < vx || rect.Y < vy || (long)rect.X + rect.Width > vx + GetSystemMetrics(78) ||
            (long)rect.Y + rect.Height > vy + GetSystemMetrics(79))
            throw new InvalidOperationException("Lens is outside the virtual desktop. Move it onto a connected display.");
        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, rect.Width, rect.Height);
        if (screen == 0 || memory == 0 || bitmap == 0)
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        nint old = SelectObject(memory, bitmap);
        try
        {
            // CAPTUREBLT includes third-party layered windows; our windows must be excluded
            // with WDA_EXCLUDEFROMCAPTURE, checked by UI before it authorizes observation.
            if (!BitBlt(memory, 0, 0, rect.Width, rect.Height, screen, rect.X, rect.Y, 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SelectObject(memory, old);
            old = 0;
            var info = new BitmapInfo
            {
                Size = 40,
                Width = rect.Width,
                Height = -rect.Height,
                Planes = 1,
                BitCount = 32
            };
            var pixels = new byte[checked(rect.Width * rect.Height * 4)];
            if (GetDIBits(memory, bitmap, 0, (uint)rect.Height, pixels, ref info, 0) != rect.Height)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return pixels;
        }
        finally
        {
            if (old != 0) SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPels, YPels;
        public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint op);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
}
