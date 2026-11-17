using Glimpse.Contracts;

namespace Glimpse.Core;

/// <summary>Whites out excluded pixels without changing OCR coordinates or image dimensions.</summary>
public static class FrameMasker
{
    public static List<PixelRect> Validate(IEnumerable<PixelRect> regions, int width, int height)
    {
        var result = regions.Take(9).ToList();
        if (result.Count > 8 || result.Any(r => r.X < 0 || r.Y < 0 || r.Width < 1 || r.Height < 1 ||
            (long)r.X + r.Width > width || (long)r.Y + r.Height > height))
            throw new InvalidOperationException("忽略区域必须位于观察框内，最多可设置 8 个区域。");
        return result;
    }

    public static void Apply(byte[] bgra, int width, int height, IReadOnlyList<PixelRect> regions)
    {
        if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("Invalid BGRA frame.");
        foreach (var r in Validate(regions, width, height))
            for (int y = r.Y; y < r.Y + r.Height; y++)
                bgra.AsSpan((y * width + r.X) * 4, r.Width * 4).Fill(255);
    }

    public static bool Intersects(TextBox bounds, PixelRect region) =>
        bounds.X < region.X + region.Width && bounds.X + bounds.Width > region.X &&
        bounds.Y < region.Y + region.Height && bounds.Y + bounds.Height > region.Y;
}

/// <summary>Invalidates a fixed-screen mask only after broad motion outside it, not a blinking control.</summary>
public sealed class MaskMotionGuard
{
    private byte[]? reference;
    public void Reset() => reference = null;
    public bool HasGlobalMovement(byte[] bgra, int width, int height, IReadOnlyList<PixelRect> regions)
    {
        const int columns = 128, rows = 80;
        var sample = new byte[columns * rows];
        int eligible = 0, changed = 0, eligibleRows = 0, changedRows = 0;
        for (int y = 0; y < rows; y++)
        {
            int rowEligible = 0, rowChanged = 0;
            for (int x = 0; x < columns; x++)
            {
                int px = x * width / columns, py = y * height / rows;
                if (regions.Any(r => px >= r.X && px < r.X + r.Width && py >= r.Y && py < r.Y + r.Height)) continue;
                int index = (py * width + px) * 4;
                byte luminance = (byte)((bgra[index] * 29 + bgra[index + 1] * 150 + bgra[index + 2] * 77) >> 8);
                sample[y * columns + x] = luminance;
                rowEligible++;
                if (reference is not null && Math.Abs(luminance - reference[y * columns + x]) > 18) rowChanged++;
            }
            eligible += rowEligible; changed += rowChanged;
            if (rowEligible > 0) eligibleRows++;
            if (rowChanged >= Math.Max(2, rowEligible / 25)) changedRows++;
        }
        reference ??= sample;
        return eligible > 0 && changed > eligible * .025 && changedRows > eligibleRows * .40;
    }
}
