using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimpse.Contracts;
using Glimpse.Worker;

namespace Glimpse.Windows.Probe;

internal static class OcrCorpusProbe
{
    public static async Task Run()
    {
        Console.Error.WriteLine("Windows OCR maximum dimension: " + global::Windows.Media.Ocr.OcrEngine.MaxImageDimension);
        var reports = new List<object>();
        var totals = new Dictionary<int, (int Errors, int Characters)>();
        var cases = new[]
        {
            ("chinese", "zh-Hans-CN", "中文阅读测试：保留每一次稳定观察。\n请保留原始文字，不要删除任何证据。"),
            ("mixed", "zh-Hans-CN", "本地识别测试，支持中文和English混合内容123。\nDo not change 12345 or remove the word not."),
            ("english", "en-US", "Do not change 12345 or remove the word not.\nKeep every original observation and its evidence.")
        };
        foreach (var (name, language, text) in cases)
        {
            if (!WindowsOcr.Languages.Contains(language)) throw new InvalidOperationException($"Corpus requires installed OCR language {language}.");
            foreach (int font in new[] { 10, 12, 16, 24 })
            {
                var pixels = Render(text, font);
                foreach (int scale in new[] { 0, 1, 2, 3 })
                {
                    var engine = new WindowsOcr(language, scale);
                    var timer = Stopwatch.StartNew();
                    var blocks = await engine.RecognizeAsync(pixels, 1100, 160, CancellationToken.None);
                    string recognized = string.Join("\n", blocks.Select(b => b.Text));
                    string expected = Normalize(text), actual = Normalize(recognized);
                    int errors = EditDistance(expected, actual);
                    totals.TryGetValue(scale, out var total);
                    totals[scale] = (total.Errors + errors, total.Characters + expected.Length);
                    bool boxesInsideImage = blocks.All(b => b.Bounds.X >= 0 && b.Bounds.Y >= 0 &&
                        b.Bounds.X + b.Bounds.Width <= 1101 && b.Bounds.Y + b.Bounds.Height <= 161);
                    if (!boxesInsideImage) throw new InvalidOperationException("Scaled OCR returned incorrect physical-pixel boxes.");
                    reports.Add(new
                    {
                        name,
                        language,
                        fontPixels = font,
                        scale,
                        errors,
                        characters = expected.Length,
                        cer = errors / (double)expected.Length,
                        elapsedMs = timer.ElapsedMilliseconds,
                        boxesInsideImage,
                        recognized
                    });
                }
            }
        }
        var blank = await new WindowsOcr("en-US", 2).RecognizeAsync(Render("", 12), 1100, 160, CancellationToken.None);
        if (blank.Count != 0) throw new InvalidOperationException("Blank frame produced OCR text.");
        double autoCer = totals[0].Errors / (double)totals[0].Characters;
        double nativeCer = totals[1].Errors / (double)totals[1].Characters;
        bool passed = autoCer <= .10 && autoCer <= nativeCer;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            corpus = "rendered-ocr-v1",
            passed,
            thresholds = "aggregate auto CER <= 0.10 and <= native CER; blank emits zero blocks; all boxes inside original image",
            size = "1100x160",
            fonts = "Microsoft YaHei UI, 96 DPI",
            normalization = "NFKC and whitespace removed for CER only; source text preserved",
            blankPassed = true,
            aggregate = totals.Select(p => new
            {
                scale = p.Key,
                errors = p.Value.Errors,
                characters = p.Value.Characters,
                cer = p.Value.Errors / (double)p.Value.Characters
            }),
            reports
        }, new JsonSerializerOptions(Envelope.Json) { WriteIndented = true }));
        if (!passed) throw new InvalidOperationException("Rendered OCR corpus exceeds its fixed acceptance thresholds.");
    }
    private static byte[] Render(string text, int font)
    {
        var panel = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(16),
            Width = 1100,
            Height = 160,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = font,
                Foreground = Brushes.Black,
                LineHeight = font * 1.8
            }
        };
        panel.Measure(new(1100, 160)); panel.Arrange(new(0, 0, 1100, 160)); panel.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1100, 160, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var pixels = new byte[1100 * 160 * 4]; bitmap.CopyPixels(pixels, 1100 * 4, 0);
        return pixels;
    }
    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
    private static int EditDistance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1]; current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(previous[j] + 1, Math.Min(current[j - 1] + 1,
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
            previous = current;
        }
        return previous[^1];
    }
}
