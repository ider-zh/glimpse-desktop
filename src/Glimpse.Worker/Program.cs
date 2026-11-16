using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Glimpse.Contracts;
using Glimpse.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Glimpse.Worker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "--ocr") return await OcrFile(args[1], args.ElementAtOrDefault(2));
            if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--data")
            {
                Console.Error.WriteLine("Usage: Glimpse.Worker --pipe <name> --data <directory> | --ocr <image> [language]");
                return 2;
            }
            var repository = new SessionRepository(Path.Combine(args[3], "sessions.db"));
            string preferencePath = Path.Combine(args[3], "ocr-language.txt");
            string? language = Environment.GetEnvironmentVariable("GLIMPSE_OCR_LANGUAGE") ??
                (File.Exists(preferencePath) ? (await File.ReadAllTextAsync(preferencePath)).Trim() : null);
            using var stop = new CancellationTokenSource();
            await using var pipe = new NamedPipeServerStream(args[1], PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await pipe.WaitForConnectionAsync(connectTimeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var writing = new SemaphoreSlim(1);
            async Task Send(Envelope envelope)
            {
                await writing.WaitAsync(stop.Token);
                try { await writer.WriteLineAsync(JsonSerializer.Serialize(envelope, Envelope.Json)); }
                finally { writing.Release(); }
            }
            await using var service = new ReaderService(repository, update => Send(Envelope.Create("update", update,
                update.Session?.Id, update.Generation)), language);
            var loop = service.RunAsync(stop.Token);
            await Send(Envelope.Create("ready", new { languages = WindowsOcr.Languages, language }));
            var replies = new Dictionary<Guid, Envelope>();
            try
            {
                while (await reader.ReadLineAsync(stop.Token) is { } line)
                {
                    if (line.Length > 1024 * 1024) throw new InvalidDataException("IPC message too large.");
                    var request = JsonSerializer.Deserialize<Envelope>(line, Envelope.Json) ?? throw new InvalidDataException();
                    if (request.ProtocolVersion != Envelope.Version) throw new InvalidDataException("Protocol version mismatch.");
                    if (replies.TryGetValue(request.RequestId, out var cached)) { await Send(cached); continue; }
                    Envelope reply;
                    try
                    {
                        switch (request.MessageType)
                        {
                            case "list": break;
                            case "language":
                                string selectedLanguage = request.Payload.GetProperty("language").GetString() ?? "";
                                await service.SetLanguageAsync(selectedLanguage, stop.Token);
                                await File.WriteAllTextAsync(preferencePath + ".tmp", selectedLanguage, stop.Token);
                                File.Move(preferencePath + ".tmp", preferencePath, true);
                                break;
                            case "new":
                                var start = request.Read<StartRequest>();
                                if (!start.Rect.IsValid) throw new InvalidOperationException("Invalid lens rectangle.");
                                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                await service.SetSessionAsync(new(Guid.NewGuid(), start.Title.Trim().Length == 0 ? "Untitled session" : start.Title[..Math.Min(120, start.Title.Length)],
                                    "paused", now, now, start.Rect, 0, [], [], []), false, stop.Token);
                                break;
                            case "resume":
                                var saved = await repository.LoadAsync(request.Read<SessionRequest>().Id, stop.Token) ??
                                    throw new InvalidOperationException("Session no longer exists.");
                                // Resume is deliberately paused until UI has restored and excluded the lens.
                                await service.SetSessionAsync(saved, false, stop.Token);
                                break;
                            case "pause": await service.PauseAsync(true, stop.Token); break;
                            case "observe": await service.PauseAsync(false, stop.Token); break;
                            case "rect": await service.SetRectAsync(request.Read<RectRequest>().Rect, stop.Token); break;
                            case "refresh": await service.PauseAsync(false, stop.Token); break;
                            case "delete":
                                var id = request.Read<SessionRequest>().Id;
                                await service.DetachAsync(id, stop.Token);
                                await repository.DeleteAsync(id, stop.Token);
                                break;
                            case "ping":
                                await Send(Envelope.Create("pong", new { }, request: request.RequestId));
                                continue;
                            default: throw new InvalidOperationException("Unknown command.");
                        }
                        reply = Envelope.Create("sessions", await repository.ListAsync(stop.Token), request: request.RequestId);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { reply = Envelope.Create("error", new { message = ReaderService.SafeError(ex) }, request: request.RequestId); }
                    if (replies.Count >= 128) replies.Remove(replies.Keys.First());
                    replies.Add(request.RequestId, reply);
                    await Send(reply);
                }
            }
            finally
            {
                stop.Cancel();
                try { await loop; } catch (OperationCanceledException) { }
            }
            return 0;
        }
        catch (Exception ex)
        {
            // No payloads, OCR text, screenshots or unredacted exception messages in logs.
            Console.Error.WriteLine($"Worker stopped: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return 1;
        }
    }
    private static async Task<int> OcrFile(string path, string? language)
    {
        var data = await File.ReadAllBytesAsync(path);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        var engine = new WindowsOcr(language);
        var timer = Stopwatch.StartNew();
        var blocks = await engine.RecognizeAsync(pixels, bitmap.PixelWidth, bitmap.PixelHeight, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { engine = engine.Name, elapsedMs = timer.ElapsedMilliseconds, blocks }, Envelope.Json));
        return 0;
    }
}
