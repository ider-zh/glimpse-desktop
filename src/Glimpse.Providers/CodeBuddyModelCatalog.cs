using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Glimpse.Contracts;

namespace Glimpse.Providers;

// ACP metadata only: initialize -> blank ephemeral session/new -> stop. Never
// sends session/prompt, OCR context, credentials, filesystem or terminal tools.
public static class CodeBuddyModelCatalog
{
    public static async Task<AgentModelCatalog> LoadAsync(AgentProviderProfile profile, CancellationToken cancellation = default)
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { return await LoadWithStartAsync(CreateStartInfo(profile, directory), profile.Id, cancellation); }
        finally
        {
            try { Directory.Delete(directory, false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    internal static ProcessStartInfo CreateStartInfo(AgentProviderProfile profile, string directory)
    {
        var start = new CodeBuddyCliProvider(profile).CreateStartInfo(new("", "", "", ""), directory);
        start.ArgumentList.Clear();
        if (!string.IsNullOrWhiteSpace(profile.CliHostPath)) start.ArgumentList.Add(profile.CliPath);
        foreach (string argument in new[] { "--acp", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
            "--setting-sources", "", "--settings", "{\"disableAllHooks\":true,\"allowUntrustedFrontmatterHooks\":false}", "--no-session-persistence" })
            start.ArgumentList.Add(argument);
        return start;
    }
    internal static async Task<AgentModelCatalog> LoadWithStartAsync(ProcessStartInfo start, string providerId, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = new Process { StartInfo = start };
        bool started = false;
        Task? diagnostics = null;
        bool authenticationRequired = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            process.Start(); started = true;
            using var kill = deadline.Token.Register(() => Stop(process));
            diagnostics = Task.Run(async () =>
            {
                int total = 0; var chars = new char[1024]; string previous = "";
                for (;;)
                {
                    int count = await process.StandardError.ReadAsync(chars, deadline.Token);
                    if (count == 0) return;
                    total += count;
                    if (total > 1024 * 1024) { Stop(process); return; }
                    string text = previous + new string(chars, 0, count);
                    if (CodeBuddyCliProvider.AuthenticationRequired(text)) authenticationRequired = true;
                    previous = text[^Math.Min(text.Length, 256)..];
                }
            }, CancellationToken.None);
            var frames = new BoundedFrames(process.StandardOutput);
            await Write(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            { protocolVersion = 1, clientInfo = new { name = "glimpse-model-discovery", version = "0.1" }, clientCapabilities = new { } } });
            using (await Response(1)) { }
            await Write(new { jsonrpc = "2.0", id = 2, method = "session/new", @params = new { cwd = start.WorkingDirectory, mcpServers = Array.Empty<object>() } });
            using var result = await Response(2);
            return Parse(result.RootElement, providerId);

            async Task Write<T>(T value)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), deadline.Token);
                await process.StandardInput.FlushAsync(deadline.Token);
            }
            async Task<JsonDocument> Response(int expected)
            {
                for (int messages = 0; messages < 512; messages++)
                {
                    string? line = await frames.ReadAsync(deadline.Token);
                    if (line is null) throw Failure(authenticationRequired);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var message = JsonDocument.Parse(line);
                    var root = message.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw Failure(false);
                    if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var requestId))
                    {
                        // No client capability is offered, even if a future CLI asks.
                        await Write(new { jsonrpc = "2.0", id = requestId, error = new { code = -32601, message = "Client tools are unavailable." } });
                        continue;
                    }
                    if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int value) || value != expected) continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        bool auth = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var detail) &&
                            detail.ValueKind == JsonValueKind.String && CodeBuddyCliProvider.AuthenticationRequired(detail.GetString()!);
                        throw Failure(auth || authenticationRequired);
                    }
                    if (!root.TryGetProperty("result", out var body)) throw Failure(false);
                    return JsonDocument.Parse(body.GetRawText());
                }
                throw Failure(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new InvalidOperationException("读取 CodeBuddy 模型列表超时。请检查 CLI 登录和网络后刷新；也可直接填写模型 ID。"); }
        catch (Exception ex) when (ex is JsonException or IOException or System.ComponentModel.Win32Exception)
        { throw Failure(authenticationRequired); }
        finally
        {
            if (started)
            {
                Stop(process);
                await process.WaitForExitAsync(CancellationToken.None);
                if (diagnostics is not null)
                    try { await diagnostics; } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
        }
    }
    private static void Stop(Process process)
    { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    private static InvalidOperationException Failure(bool authentication) => new(authentication ?
        "CodeBuddy 需要重新登录。请打开登录终端完成验证后刷新模型列表。" :
        "无法读取 CodeBuddy 模型列表。请检查 CLI 版本、登录和网络后刷新；也可直接填写模型 ID。");

    internal static AgentModelCatalog Parse(JsonElement session, string providerId)
    {
        if (session.ValueKind != JsonValueKind.Object || !session.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object ||
            !models.TryGetProperty("availableModels", out var available) || available.ValueKind != JsonValueKind.Array || available.GetArrayLength() > 256)
            throw Failure(false);
        var values = new List<AgentModelOption>();
        foreach (var item in available.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("modelId", out var id) || id.ValueKind != JsonValueKind.String ||
                !ValidId(id.GetString())) continue;
            string modelId = id.GetString()!;
            string name = item.TryGetProperty("name", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() ?? modelId : modelId;
            name = new string(name.Where(c => !char.IsControl(c)).Take(160).ToArray()).Trim();
            if (!values.Any(m => m.Id == modelId)) values.Add(new(modelId, name.Length == 0 ? modelId : name));
        }
        if (values.Count == 0) throw Failure(false);
        string? current = models.TryGetProperty("currentModelId", out var selected) && selected.ValueKind == JsonValueKind.String && ValidId(selected.GetString()) ? selected.GetString() : null;
        return new(providerId, values, current);
    }
    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
    private sealed class BoundedFrames(StreamReader reader)
    {
        private readonly StringBuilder buffered = new();
        private readonly char[] chars = new char[4096];
        private int total;
        public async Task<string?> ReadAsync(CancellationToken cancellation)
        {
            for (;;)
            {
                for (int i = 0; i < buffered.Length; i++)
                    if (buffered[i] == '\n')
                    {
                        string line = buffered.ToString(0, i); buffered.Remove(0, i + 1); return line;
                    }
                int count = await reader.ReadAsync(chars, cancellation);
                if (count == 0)
                {
                    if (buffered.Length == 0) return null;
                    string remaining = buffered.ToString(); buffered.Clear(); return remaining;
                }
                total += count;
                if (total > 2 * 1024 * 1024 || buffered.Length + count > 1024 * 1024) throw Failure(false);
                buffered.Append(chars, 0, count);
            }
        }
    }
}
