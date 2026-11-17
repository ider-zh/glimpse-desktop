using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Glimpse.Contracts;
using Glimpse.Storage;
using Glimpse.Providers;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Glimpse.Worker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "--ocr") return await OcrFile(args[1], args.ElementAtOrDefault(2),
                int.TryParse(args.ElementAtOrDefault(3), out int cliScale) ? cliScale : 0);
            if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--data")
            {
                Console.Error.WriteLine("Usage: Glimpse.Worker --pipe <name> --data <directory> | --ocr <image> [language] [scale: 0=auto,1=native,2=double]");
                return 2;
            }
            var repository = new SessionRepository(Path.Combine(args[3], "sessions.db"));
            string preferencePath = Path.Combine(args[3], "ocr-language.txt");
            string? language = Environment.GetEnvironmentVariable("GLIMPSE_OCR_LANGUAGE") ??
                (File.Exists(preferencePath) ? (await File.ReadAllTextAsync(preferencePath)).Trim() : null);
            string scalePath = Path.Combine(args[3], "ocr-scale.txt");
            string? configuredScale = Environment.GetEnvironmentVariable("GLIMPSE_OCR_SCALE") ??
                (File.Exists(scalePath) ? (await File.ReadAllTextAsync(scalePath)).Trim() : null);
            int scale = int.TryParse(configuredScale, out int parsedScale) && parsedScale is >= 0 and <= 2 ? parsedScale : 0;
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
                update.Session?.Id, update.Generation)), language, initialScale: scale);
            var loop = service.RunAsync(stop.Token);
            var providerSettings = new ProviderSettingsStore(args[3]);
            using var agent = new AgentController(providerSettings, repository, service.GetCurrentSessionAsync);
            await using var login = new CliLoginController(providerSettings);
            await Send(Envelope.Create("ready", new { languages = WindowsOcr.Languages, language, scale }));
            var replies = new ConcurrentDictionary<Guid, Envelope>();
            var inFlight = new ConcurrentDictionary<Guid, byte>();
            var background = new List<Task>();
            int modelCatalogRunning = 0;
            int dynamicAnalysisRunning = 0;
            void Remember(Envelope response)
            {
                if (replies.Count >= 128) replies.TryRemove(replies.Keys.FirstOrDefault(), out _);
                replies[response.RequestId] = response;
            }
            try
            {
                while (await reader.ReadLineAsync(stop.Token) is { } line)
                {
                    if (line.Length > 1024 * 1024) throw new InvalidDataException("IPC message too large.");
                    var request = JsonSerializer.Deserialize<Envelope>(line, Envelope.Json) ?? throw new InvalidDataException();
                    if (request.ProtocolVersion != Envelope.Version) throw new InvalidDataException("Protocol version mismatch.");
                    if (replies.TryGetValue(request.RequestId, out var cached)) { await Send(cached); continue; }
                    if (request.MessageType == "agent-models-get")
                    {
                        if (inFlight.ContainsKey(request.RequestId)) continue;
                        if (login.HasSession || Interlocked.CompareExchange(ref modelCatalogRunning, 1, 0) != 0)
                        {
                            await Send(Envelope.Create("error", new { message = "请先结束登录终端，或等待当前模型列表读取完成后刷新。" }, request: request.RequestId));
                            continue;
                        }
                        if (!inFlight.TryAdd(request.RequestId, 0)) { Volatile.Write(ref modelCatalogRunning, 0); continue; }
                        background.Add(Task.Run(async () =>
                        {
                            Envelope response;
                            try
                            {
                                var configured = await providerSettings.LoadPublicAsync(stop.Token);
                                var profile = configured.Providers.Single(p => p.Id == configured.SelectedProviderId);
                                if (profile.Kind != "codebuddy-cli") throw new InvalidOperationException("请先保存并切换到 CodeBuddy CLI。");
                                var catalog = await CodeBuddyModelCatalog.LoadAsync(profile, stop.Token);
                                response = Envelope.Create("agent-models", catalog, request: request.RequestId);
                            }
                            catch (Exception ex)
                            {
                                response = Envelope.Create("error", new { message = ex is InvalidOperationException ? ex.Message :
                                    "模型列表读取未完成。请检查 CLI 登录和网络后刷新。" }, request: request.RequestId);
                            }
                            finally { Volatile.Write(ref modelCatalogRunning, 0); }
                            Remember(response); inFlight.TryRemove(request.RequestId, out _);
                            try { await Send(response); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
                        }, CancellationToken.None));
                        continue;
                    }
                    if (request.MessageType == "agent-run")
                    {
                        if (Volatile.Read(ref dynamicAnalysisRunning) != 0)
                        {
                            await Send(Envelope.Create("error", new { message = "请等待动态区域分析完成，再重新生成。" }, request: request.RequestId));
                            continue;
                        }
                        if (login.HasSession)
                        {
                            await Send(Envelope.Create("error", new { message = "请先结束登录终端，再重新预览、生成。" }, request: request.RequestId));
                            continue;
                        }
                        if (!inFlight.TryAdd(request.RequestId, 0)) continue;
                        // Model work must never occupy the command loop: heartbeat,
                        // pause, capture updates and cancellation remain serviceable.
                        background.Add(Task.Run(async () =>
                        {
                            Envelope response;
                            try
                            {
                                var result = await agent.RunAsync(request.Payload.GetProperty("previewId").GetGuid(), stop.Token);
                                response = Envelope.Create("agent-result", result, result.SessionId, request: request.RequestId);
                            }
                            catch (Exception ex)
                            {
                                response = Envelope.Create("error", new
                                {
                                    message = ex is InvalidOperationException ? ex.Message :
                                    "Agent 调用未完成，请检查配置、重新预览后重试。"
                                }, request: request.RequestId);
                            }
                            Remember(response); inFlight.TryRemove(request.RequestId, out _);
                            try { await Send(response); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
                        }, CancellationToken.None));
                        continue;
                    }
                    if (request.MessageType == "analyze-dynamic-regions")
                    {
                        if (Interlocked.CompareExchange(ref dynamicAnalysisRunning, 1, 0) != 0)
                        {
                            await Send(Envelope.Create("error", new { message = "正在分析动态区域，请稍候。" }, request: request.RequestId));
                            continue;
                        }
                        if (!inFlight.TryAdd(request.RequestId, 0))
                        { Volatile.Write(ref dynamicAnalysisRunning, 0); continue; }
                        agent.Invalidate();
                        background.Add(Task.Run(async () =>
                        {
                            Envelope response;
                            try
                            {
                                await service.AnalyzeDynamicRegionsAsync(stop.Token);
                                response = Envelope.Create("dynamic-regions-analyzed", new { }, request: request.RequestId);
                            }
                            catch (Exception ex)
                            {
                                response = Envelope.Create("error", new { message = ex is InvalidOperationException ? ex.Message :
                                    "动态区域分析已中止。请重试或手动框选视频。" }, request: request.RequestId);
                            }
                            finally { Volatile.Write(ref dynamicAnalysisRunning, 0); }
                            Remember(response); inFlight.TryRemove(request.RequestId, out _);
                            try { await Send(response); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
                        }, CancellationToken.None));
                        continue;
                    }
                    Envelope reply;
                    try
                    {
                        switch (request.MessageType)
                        {
                            case "list": break;
                            case "agent-login-start":
                                if (agent.IsRunning) throw new InvalidOperationException("请先取消或等待正在运行的生成，再打开登录终端。");
                                agent.Invalidate();
                                var startedLogin = await login.StartAsync(stop.Token);
                                // Retain only session ownership metadata for idempotent
                                // start retries, never a terminal transcript or URL.
                                reply = Envelope.Create("agent-login", startedLogin with { ScreenText = "", Links = [], TerminalOutput = "", OutputStart = 0 }, request: request.RequestId);
                                Remember(reply); await Send(reply); continue;
                            case "agent-login-read":
                                reply = Envelope.Create("agent-login", login.Read(request.Read<CliLoginRequest>().LoginId), request: request.RequestId);
                                await Send(reply); continue;
                            case "agent-login-input":
                                var terminalInput = request.Read<CliLoginInput>();
                                await login.SendAsync(terminalInput, stop.Token);
                                reply = Envelope.Create("agent-login-input-sent", new { }, request: request.RequestId);
                                Remember(reply); await Send(reply); continue;
                            case "agent-login-resize":
                                login.Resize(request.Read<CliLoginResize>());
                                reply = Envelope.Create("agent-login-resized", new { }, request: request.RequestId);
                                Remember(reply); await Send(reply); continue;
                            case "agent-login-stop":
                                await login.StopAsync(request.Read<CliLoginRequest>().LoginId);
                                agent.Invalidate();
                                reply = Envelope.Create("agent-login-stopped", new { }, request: request.RequestId);
                                Remember(reply); await Send(reply); continue;
                            case "agent-settings-get":
                                reply = Envelope.Create("agent-settings", await providerSettings.LoadPublicAsync(stop.Token), request: request.RequestId);
                                Remember(reply); await Send(reply); continue;
                            case "agent-settings-save":
                                if (login.HasSession) throw new InvalidOperationException("请先结束登录终端，再修改引擎设置。");
                                agent.Invalidate();
                                reply = Envelope.Create("agent-settings", await providerSettings.SaveAsync(request.Read<AgentProviderUpdate>(), stop.Token), request: request.RequestId);
                                Remember(reply); await Send(reply); continue;
                            case "agent-preview":
                                if (Volatile.Read(ref dynamicAnalysisRunning) != 0)
                                    throw new InvalidOperationException("请等待动态区域分析完成，再重新发送任务。");
                                if (login.HasSession) throw new InvalidOperationException("请先结束登录终端，再预览上下文。");
                                reply = Envelope.Create("agent-preview", await agent.PreviewAsync(request.Read<AgentTaskRequest>(), stop.Token), request: request.RequestId);
                                Remember(reply); await Send(reply); continue;
                            case "agent-cancel":
                                agent.Cancel(request.Payload.TryGetProperty("previewId", out var cancelId) ? cancelId.GetGuid() : null);
                                reply = Envelope.Create("agent-canceled", new { }, request: request.RequestId);
                                Remember(reply); await Send(reply); continue;
                            case "agent-history":
                                reply = Envelope.Create("agent-history", await repository.ListAgentResultsAsync(request.Read<SessionRequest>().Id, cancellation: stop.Token), request: request.RequestId);
                                Remember(reply); await Send(reply); continue;
                            case "ocr-mode":
                                int selectedScale = request.Payload.GetProperty("scale").GetInt32();
                                await service.SetScaleAsync(selectedScale, stop.Token);
                                await File.WriteAllTextAsync(scalePath + ".tmp", selectedScale.ToString(System.Globalization.CultureInfo.InvariantCulture), stop.Token);
                                File.Move(scalePath + ".tmp", scalePath, true);
                                break;
                            case "language":
                                string selectedLanguage = request.Payload.GetProperty("language").GetString() ?? "";
                                await service.SetLanguageAsync(selectedLanguage, stop.Token);
                                await File.WriteAllTextAsync(preferencePath + ".tmp", selectedLanguage, stop.Token);
                                File.Move(preferencePath + ".tmp", preferencePath, true);
                                break;
                            case "new":
                                agent.Invalidate();
                                var start = request.Read<StartRequest>();
                                if (!start.Rect.IsValid) throw new InvalidOperationException("Invalid lens rectangle.");
                                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                await service.SetSessionAsync(new(Guid.NewGuid(), start.Title.Trim().Length == 0 ? "Untitled session" : start.Title[..Math.Min(120, start.Title.Length)],
                                    "paused", now, now, start.Rect, 0, [], [], []), false, stop.Token);
                                break;
                            case "resume":
                                agent.Invalidate();
                                var saved = await repository.LoadAsync(request.Read<SessionRequest>().Id, stop.Token) ??
                                    throw new InvalidOperationException("Session no longer exists.");
                                // Resume is deliberately paused until UI has restored and excluded the lens.
                                await service.SetSessionAsync(saved, false, stop.Token);
                                break;
                            case "pause": await service.PauseAsync(true, stop.Token); break;
                            case "observe": await service.PauseAsync(false, stop.Token); break;
                            case "rect": await service.SetRectAsync(request.Read<RectRequest>().Rect, stop.Token); break;
                            case "ignore-regions":
                                agent.Invalidate();
                                await service.SetIgnoreRegionsAsync(request.Read<IgnoreRegionsRequest>().Regions, stop.Token); break;
                            case "refresh": await service.PauseAsync(false, stop.Token); break;
                            case "delete":
                                agent.Invalidate();
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
                    Remember(reply);
                    await Send(reply);
                }
            }
            finally
            {
                agent.Cancel();
                await login.DisposeAsync();
                stop.Cancel();
                try { await loop; } catch (OperationCanceledException) { }
                await Task.WhenAll(background);
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
    private static async Task<int> OcrFile(string path, string? language, int scale)
    {
        var data = await File.ReadAllBytesAsync(path);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        var engine = new WindowsOcr(language, scale);
        var timer = Stopwatch.StartNew();
        var blocks = await engine.RecognizeAsync(pixels, bitmap.PixelWidth, bitmap.PixelHeight, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { engine = engine.Name, elapsedMs = timer.ElapsedMilliseconds, blocks }, Envelope.Json));
        return 0;
    }
}
