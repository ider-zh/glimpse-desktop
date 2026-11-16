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
    public string Name => "Windows.Media.Ocr/" + engine.RecognizerLanguage.LanguageTag;
    public static string[] Languages => OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
    public WindowsOcr(string? language = null)
    {
        engine = (language is null ? OcrEngine.TryCreateFromUserProfileLanguages() :
            OcrEngine.TryCreateFromLanguage(new Language(language))) ??
            throw new InvalidOperationException("No local OCR language is installed. Install a language OCR capability in Windows Settings, then restart Glimpse.");
    }
    public async Task<List<OcrBlock>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension)
            throw new InvalidOperationException($"OCR region exceeds {OcrEngine.MaxImageDimension} pixels. Resize the lens.");
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8,
            width, height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellation);
        cancellation.ThrowIfCancellationRequested();
        return result.Lines.Where(l => l.Words.Count > 0).Select(line =>
        {
            double x = line.Words.Min(w => w.BoundingRect.X), y = line.Words.Min(w => w.BoundingRect.Y);
            double right = line.Words.Max(w => w.BoundingRect.Right), bottom = line.Words.Max(w => w.BoundingRect.Bottom);
            return new OcrBlock(line.Text, new(x, y, right - x, bottom - y));
        }).ToList();
    }
}
