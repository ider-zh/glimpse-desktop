namespace Glimpse.Core;

public record OcrImage(byte[] Pixels, int Width, int Height, double ScaleX, double ScaleY)
{
    public static bool ShouldEnhance(IEnumerable<double> glyphHeights, byte[] bgra)
    {
        var heights = glyphHeights.Where(h => h > 0).Order().ToArray();
        if (heights.Length > 0) return heights[heights.Length / 2] <= 14;
        // No words may mean very small text. Retry only if sampled pixels show contrast;
        // a uniform blank frame should not pay for a second recognition pass.
        int minimum = 255, maximum = 0;
        int stride = Math.Max(4, bgra.Length / 4096 / 4 * 4);
        for (int i = 0; i + 2 < bgra.Length; i += stride)
        {
            int luminance = (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8;
            minimum = Math.Min(minimum, luminance); maximum = Math.Max(maximum, luminance);
        }
        return maximum - minimum > 24;
    }
    // Small glyphs benefit from enlargement before OCR. The maximum comes from the
    // selected engine, and bounding boxes must be mapped back by ScaleX/ScaleY.
    public static OcrImage Prepare(byte[] bgra, int width, int height, int requestedScale,
        int maximumDimension, CancellationToken cancellation = default)
    {
        if (width <= 0 || height <= 0 || bgra.Length != checked(width * height * 4) ||
            requestedScale is < 1 or > 3 || maximumDimension is < 1 or > 8192 ||
            width > maximumDimension || height > maximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid OCR image dimensions or scale.");
        cancellation.ThrowIfCancellationRequested();
        double factor = Math.Min(requestedScale, maximumDimension / (double)Math.Max(width, height));
        // Enhancement has a 32 MiB output budget. Already-large images stay native size.
        double budgetFactor = Math.Sqrt(8_388_608d / width / height);
        bool memoryBound = budgetFactor < factor;
        factor = Math.Max(1, Math.Min(factor, budgetFactor));
        int w = Math.Min(maximumDimension, (int)(memoryBound ? Math.Floor(width * factor) : Math.Round(width * factor)));
        int h = Math.Min(maximumDimension, (int)(memoryBound ? Math.Floor(height * factor) : Math.Round(height * factor)));
        if (w == width && h == height) return new(bgra, width, height, 1, 1);
        var output = new byte[checked(w * h * 4)];
        double scaleX = w / (double)width, scaleY = h / (double)height;
        var left = new int[w]; var right = new int[w]; var fraction = new double[w];
        for (int x = 0; x < w; x++)
        {
            double source = Math.Clamp((x + .5) / scaleX - .5, 0, width - 1);
            left[x] = (int)source; right[x] = Math.Min(left[x] + 1, width - 1); fraction[x] = source - left[x];
        }
        for (int y = 0; y < h; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            double source = Math.Clamp((y + .5) / scaleY - .5, 0, height - 1);
            int top = (int)source, bottom = Math.Min(top + 1, height - 1);
            double fy = source - top;
            for (int x = 0; x < w; x++)
            {
                int a = (top * width + left[x]) * 4, b = (top * width + right[x]) * 4;
                int c = (bottom * width + left[x]) * 4, d = (bottom * width + right[x]) * 4;
                int target = (y * w + x) * 4;
                double fx = fraction[x];
                for (int channel = 0; channel < 4; channel++)
                {
                    double first = bgra[a + channel] * (1 - fx) + bgra[b + channel] * fx;
                    double second = bgra[c + channel] * (1 - fx) + bgra[d + channel] * fx;
                    output[target + channel] = (byte)Math.Round(first * (1 - fy) + second * fy);
                }
            }
        }
        return new(output, w, h, scaleX, scaleY);
    }
}
