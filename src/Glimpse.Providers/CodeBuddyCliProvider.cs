using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Glimpse.Agent;
using Glimpse.Contracts;

namespace Glimpse.Providers;

public sealed class CodeBuddyCliProvider : ILLMProvider
{
    private readonly AgentProviderProfile profile;
    private readonly Func<ProcessStartInfo, string, CancellationToken, Task<CliProcessResult>> run;
    public string Id => profile.Id;
    public CodeBuddyCliProvider(AgentProviderProfile profile,
        Func<ProcessStartInfo, string, CancellationToken, Task<CliProcessResult>>? runner = null)
    { this.profile = profile; run = runner ?? RunProcess; }

    // Public for offline harness inspection: no captured text or secrets are arguments.
    public ProcessStartInfo CreateStartInfo(ProviderRequest request, string workingDirectory)
    {
        if (!Path.IsPathFullyQualified(profile.CliPath) || !File.Exists(profile.CliPath))
            throw new InvalidOperationException("请配置已安装的 CodeBuddy 可执行程序或 Node.js CLI 入口。");
        bool hosted = !string.IsNullOrWhiteSpace(profile.CliHostPath);
        if (hosted && (!Path.IsPathFullyQualified(profile.CliHostPath) || !File.Exists(profile.CliHostPath) ||
            !Path.GetFileName(profile.CliHostPath).Equals("node.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("CLI 主机须为已安装的 node.exe 完整路径。");
        if (!hosted && !Path.GetExtension(profile.CliPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("直接 CLI 路径必须是 .exe；npm 安装请指定 Node.js 主机和 JavaScript 入口，不使用 cmd/PowerShell 包装脚本。");
        var start = new ProcessStartInfo(hosted ? profile.CliHostPath : profile.CliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };
        if (hosted) start.ArgumentList.Add(profile.CliPath);
        foreach (string arg in new[] { "--print", "--input-format", "text", "--output-format", "json",
            "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
            "--setting-sources", "", "--settings", "{\"disableAllHooks\":true,\"allowUntrustedFrontmatterHooks\":false}",
            "--no-session-persistence", "--max-turns", "1", "--system-prompt", request.SystemPrompt }) start.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(profile.Model)) { start.ArgumentList.Add("--model"); start.ArgumentList.Add(profile.Model); }
        start.Environment["CODEBUDDY_CODE_DISABLE_BACKGROUND_TASKS"] = "1";
        start.Environment["CODEBUDDY_SKIP_BUILTIN_MARKETPLACE"] = "1";
        start.Environment.Remove("CODEBUDDY_PLUGIN_DIRS");
        return start;
    }
    public async Task<ProviderResponse> GenerateAsync(ProviderRequest request, CancellationToken cancellation)
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        try
        {
            var result = await run(CreateStartInfo(request, directory), request.UserPrompt, timeout.Token);
            if ((result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput)) &&
                (AuthenticationRequired(result.StandardError) || AuthenticationRequired(result.StandardOutput)))
                throw new ProviderFailureException(ProviderFailureKind.CliAuthenticationRequired);
            if (result.ExitCode != 0)
                throw new ProviderFailureException(ProviderFailureKind.CliExit, result.ExitCode);
            return new(ExtractResult(result.StandardOutput), profile.Model.Length == 0 ? null : profile.Model);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("CodeBuddy CLI 响应超时，进程已停止。"); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("无法启动 CodeBuddy CLI，请检查可执行路径。"); }
        finally
        {
            // Only our fresh empty workspace can be removed. CLI persistence is disabled;
            // files a future CLI version unexpectedly creates are not recursively erased.
            try { Directory.Delete(directory, false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public static string ExtractResult(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) throw new ProviderFailureException(ProviderFailureKind.CliEmptyResponse);
        if (output.Length > 1024 * 1024) throw new ProviderFailureException(ProviderFailureKind.OutputLimit);
        try
        {
            using var json = JsonDocument.Parse(output);
            var result = json.RootElement;
            if (result.ValueKind == JsonValueKind.Array)
            {
                var messages = result.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object &&
                    e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "result").ToArray();
                if (messages.Length == 0) throw new ProviderFailureException(ProviderFailureKind.CliEmptyResponse);
                result = messages[^1];
            }
            if (result.ValueKind != JsonValueKind.Object) throw new ProviderFailureException(ProviderFailureKind.CliInvalidResponse);
            if (result.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True)
            {
                string error = result.TryGetProperty("result", out var details) && details.ValueKind == JsonValueKind.String ? details.GetString()! : "";
                if (AuthenticationRequired(error)) throw new ProviderFailureException(ProviderFailureKind.CliAuthenticationRequired);
                if (result.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String && subtype.GetString() == "error_max_turns")
                    throw new ProviderFailureException(ProviderFailureKind.CliTurnLimit);
                throw new ProviderFailureException(ProviderFailureKind.CliInvalidResponse);
            }
            if (result.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
                return structured.GetRawText();
            if (result.TryGetProperty("result", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()))
                return text.GetString()!;
            // Some versions emit the structured result directly; local schema validation
            // still decides whether it is an accepted card or a visibly invalid fallback.
            if (result.TryGetProperty("schema", out _)) return result.GetRawText();
            throw new ProviderFailureException(ProviderFailureKind.CliEmptyResponse);
        }
        catch (JsonException) { throw new ProviderFailureException(ProviderFailureKind.CliInvalidResponse); }
    }
    internal static bool AuthenticationRequired(string text) =>
        text.Contains("Authentication required", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("401", StringComparison.Ordinal) &&
        (text.Contains("/login", StringComparison.OrdinalIgnoreCase) || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase));
    internal static async Task<CliProcessResult> RunProcess(ProcessStartInfo start, string input, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = start };
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        using var kill = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        async Task<string> ReadBounded(StreamReader reader)
        {
            var text = new StringBuilder(); var chars = new char[4096];
            while (true)
            {
                int count = await reader.ReadAsync(chars, cancellation);
                if (count == 0) break;
                if (text.Length + count > 1024 * 1024)
                {
                    if (!process.HasExited) process.Kill(true);
                    throw new ProviderFailureException(ProviderFailureKind.OutputLimit);
                }
                text.Append(chars, 0, count);
            }
            return text.ToString();
        }
        var stdout = ReadBounded(process.StandardOutput);
        var stderr = ReadBounded(process.StandardError);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellation);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(cancellation), stdout, stderr);
            cancellation.ThrowIfCancellationRequested();
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException) { }
        }
    }
}
public record CliProcessResult(int ExitCode, string StandardOutput, string StandardError = "")
{
    public override string ToString() => $"CLI result: exit={ExitCode}, outputChars={StandardOutput.Length}, diagnosticChars={StandardError.Length}";
}
