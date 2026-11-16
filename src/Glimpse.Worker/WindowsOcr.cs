using System.Runtime.InteropServices.WindowsRuntime;
using Glimpse.Contracts;
using Glimpse.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Glimpse.Worker;

public sealed class WindowsOcr : IOcrEngine
{
    private readonly OcrEngine engine;
    private readonly int scale;
    public string Name => "Windows.Media.Ocr/" + engine.RecognizerLanguage.LanguageTag +
        (scale == 0 ? "/auto" : scale == 1 ? "" : $"/upscale{scale}");
    public static string[] Languages => OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
    public WindowsOcr(string? language = null, int scale = 0)
    {
        if (scale is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(scale));
        this.scale = scale;
        engine = (language is null ? OcrEngine.TryCreateFromUserProfileLanguages() :
            OcrEngine.TryCreateFromLanguage(new Language(language))) ??
            throw new InvalidOperationException("No local OCR language is installed. Install a language OCR capability in Windows Settings, then restart Glimpse.");
    }
    public async Task<List<OcrBlock>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        int maximum = Math.Min(8192, (int)OcrEngine.MaxImageDimension);
        if (width > maximum || height > maximum)
            throw new InvalidOperationException($"OCR region exceeds {maximum} pixels. Resize the lens.");
        var image = OcrImage.Prepare(bgra, width, height, Math.Max(1, scale), maximum, cancellation);
        async Task<OcrResult> Recognize(OcrImage prepared)
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(prepared.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
                prepared.Width, prepared.Height, BitmapAlphaMode.Ignore);
            return await engine.RecognizeAsync(bitmap).AsTask(cancellation);
        }
        var result = await Recognize(image);
        if (scale == 0 && OcrImage.ShouldEnhance(result.Lines.SelectMany(l => l.Words).Select(w => w.BoundingRect.Height), bgra))
        {
            var enhanced = OcrImage.Prepare(bgra, width, height, 2, maximum, cancellation);
            if (enhanced.ScaleX > 1 || enhanced.ScaleY > 1)
            {
                image = enhanced;
                result = await Recognize(image);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return result.Lines.Where(l => l.Words.Count > 0).Select(line =>
        {
            double x = line.Words.Min(w => w.BoundingRect.X), y = line.Words.Min(w => w.BoundingRect.Y);
            double right = line.Words.Max(w => w.BoundingRect.Right), bottom = line.Words.Max(w => w.BoundingRect.Bottom);
            return new OcrBlock(line.Text, new(x / image.ScaleX, y / image.ScaleY,
                (right - x) / image.ScaleX, (bottom - y) / image.ScaleY));
        }).ToList();
    }
}
