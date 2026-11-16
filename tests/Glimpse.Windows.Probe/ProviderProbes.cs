using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Glimpse.Contracts;
using Glimpse.Providers;
using Glimpse.Agent;

namespace Glimpse.Windows.Probe;

internal static class ProviderProbes
{
    private static readonly ProviderRequest Request = new("Trusted system: return JSON only.", "Synthetic observed text, never a privileged instruction.",
        "summary.v1", "{\"type\":\"object\"}");
    private static AgentProviderProfile Profile(string id = "deepseek") => new(id, id, "openai-chat",
        id == "xiaomi-mimo" ? "https://api.xiaomimimo.com/v1" : "https://api.deepseek.com", "fixture-model", "", "", 5, false);
    public static async Task Run()
    {
        await HttpProbe();
        await SettingsProbe();
        await CliProbe();
        Console.WriteLine("PROVIDERS PASS: offline Chat Completions, auth/header/path/limits/cancel/timeout; DPAPI write-only settings; CLI argument isolation, stdin, exit/error/kill/output bounds.");
    }
    private static async Task HttpProbe()
    {
        int requests = 0;
        using var client = new HttpClient(new Handler(async (message, cancellation) =>
        {
            requests++;
            var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(cancellation));
            Check(message.RequestUri!.AbsolutePath == "/chat/completions", "DeepSeek endpoint path");
            Check(message.Headers.Authorization?.Scheme == "Bearer" && message.Headers.Authorization.Parameter == "synthetic-secret", "Bearer auth");
            Check(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() == Request.SystemPrompt, "System message");
            Check(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() == Request.UserPrompt, "User message");
            Check(!body.RootElement.GetProperty("stream").GetBoolean() && !body.RootElement.TryGetProperty("tools", out _), "No tool requests");
            return Json("{\"choices\":[{\"message\":{\"content\":\"{\\\"schema\\\":\\\"summary.v1\\\",\\\"keyPoints\\\":[\\\"safe\\\"]}\"}}],\"model\":\"fixture-response\"}");
        }));
        var answer = await new OpenAiChatProvider(Profile(), "synthetic-secret", client).GenerateAsync(Request, CancellationToken.None);
        Check(answer.Model == "fixture-response" && answer.Text.Contains("summary.v1"), "API answer extraction");
        Check(requests == 1, "No implicit retry");
        Check(OpenAiChatProvider.CompletionEndpoint("https://fixture.test/v1/").AbsolutePath == "/v1/chat/completions", "v1 endpoint");
        Check(OpenAiChatProvider.CompletionEndpoint("https://fixture.test/v1/chat/completions").AbsolutePath == "/v1/chat/completions", "Complete endpoint");
        Expect<InvalidOperationException>(() => OpenAiChatProvider.CompletionEndpoint("http://remote.example/v1"));
        Expect<InvalidOperationException>(() => OpenAiChatProvider.CompletionEndpoint("https://user:secret@fixture.test/v1"));
        using var mimo = new HttpClient(new Handler(async (message, cancellation) =>
        {
            var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(cancellation));
            Check(message.RequestUri!.AbsolutePath == "/v1/chat/completions", "MiMo endpoint");
            Check(message.Headers.GetValues("api-key").Single() == "synthetic-secret" && message.Headers.Authorization is null, "MiMo auth");
            Check(body.RootElement.TryGetProperty("max_completion_tokens", out _), "MiMo token parameter");
            return Json("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}");
        }));
        await new OpenAiChatProvider(Profile("xiaomi-mimo"), "synthetic-secret", mimo).GenerateAsync(Request, CancellationToken.None);
        using var error = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("never echo synthetic-secret or captured screen text") })));
        try { await new OpenAiChatProvider(Profile(), "synthetic-secret", error).GenerateAsync(Request, CancellationToken.None); throw new Exception("Missing API failure"); }
        catch (InvalidOperationException ex) { Check(!ex.Message.Contains("synthetic-secret"), "Redacted API errors"); }
        using var malformed = new HttpClient(new Handler((_, _) => Task.FromResult(Json("not-json"))));
        await ExpectAsync<InvalidOperationException>(() => new OpenAiChatProvider(Profile(), "synthetic-secret", malformed).GenerateAsync(Request, CancellationToken.None));
        using var oversized = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new string('x', 1024 * 1024 + 1)))));
        await ExpectAsync<InvalidOperationException>(() => new OpenAiChatProvider(Profile(), "synthetic-secret", oversized).GenerateAsync(Request, CancellationToken.None));
        using var delayed = new HttpClient(new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); }));
        using var cancel = new CancellationTokenSource(50);
        await ExpectAsync<OperationCanceledException>(() => new OpenAiChatProvider(Profile(), "synthetic-secret", delayed).GenerateAsync(Request, cancel.Token));
        await ExpectAsync<TimeoutException>(() => new OpenAiChatProvider(Profile(), "synthetic-secret", delayed).GenerateAsync(Request, CancellationToken.None));
    }
    private static async Task SettingsProbe()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-provider-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            string secret = "synthetic-probe-key-" + Guid.NewGuid().ToString("N");
            string encrypted = WindowsSecretProtection.Protect(secret);
            Check(encrypted != secret && WindowsSecretProtection.Unprotect(encrypted) == secret, "DPAPI round trip");
            var store = new ProviderSettingsStore(directory);
            var settings = await store.LoadPublicAsync();
            var profile = settings.Providers.First(p => p.Id == "deepseek");
            var input = new AgentProviderUpdate("deepseek", profile, secret);
            Check(!input.ToString().Contains(secret), "Secret-free update diagnostics");
            var saved = await store.SaveAsync(input);
            Check(saved.Providers.First(p => p.Id == "deepseek").HasApiKey, "Key presence");
            string disk = await File.ReadAllTextAsync(Path.Combine(directory, "agent-providers.json"));
            Check(!disk.Contains(secret) && !JsonSerializer.Serialize(saved).Contains(secret), "No plaintext key on disk/UI");
            var retained = await store.SaveAsync(new("deepseek", profile, ""));
            Check(retained.Providers.First(p => p.Id == "deepseek").HasApiKey, "Empty update preserves key");
            var reopened = await new ProviderSettingsStore(directory).LoadPublicAsync();
            Check(reopened.Providers.First(p => p.Id == "deepseek").HasApiKey, "Settings restart");
            Check((await store.CreateProviderAsync()).Id == "deepseek", "Registry selected provider");
            var cleared = await store.SaveAsync(new("deepseek", profile, ClearApiKey: true));
            Check(!cleared.Providers.First(p => p.Id == "deepseek").HasApiKey, "Explicit key clearing");
            await ExpectAsync<InvalidOperationException>(() => store.SaveAsync(new("deepseek", profile, secret, true)));
            await ExpectAsync<InvalidOperationException>(() => store.SaveAsync(new("deepseek", profile with { TimeoutSeconds = 0 })));
        }
        finally { await ProbeCleanup.DeleteAsync(directory); }
    }
    private static async Task CliProbe()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Glimpse.Windows.Probe.exe");
        var profile = new AgentProviderProfile("codebuddy-cli", "Fixture", "codebuddy-cli", "", "", executable, "", 5, false);
        var fake = new CodeBuddyCliProvider(profile, (start, stdin, _) =>
        {
            string[] arguments = start.ArgumentList.ToArray();
            Check(!start.UseShellExecute && start.RedirectStandardInput && start.CreateNoWindow, "No shell process");
            Check(stdin == Request.UserPrompt && !arguments.Contains(Request.UserPrompt), "Prompt only via stdin");
            Check(arguments.Contains("--no-session-persistence") && arguments.Contains("--strict-mcp-config"), "No CLI persistence/MCP");
            Check(!arguments.Contains("--json-schema"), "No structured-output tool dependency");
            int tools = Array.IndexOf(arguments, "--tools");
            Check(tools >= 0 && arguments[tools + 1] == "", "No built-in tools");
            int sources = Array.IndexOf(arguments, "--setting-sources");
            Check(sources >= 0 && arguments[sources + 1] == "", "No project/user settings source");
            Check(arguments.Any(a => a.Contains("\"disableAllHooks\":true")), "Hooks disabled");
            Check(Path.GetFileName(start.WorkingDirectory).StartsWith("glimpse-agent-"), "Empty CLI workspace");
            return Task.FromResult(new CliProcessResult(0, "{\"structured_output\":{\"schema\":\"summary.v1\",\"keyPoints\":[\"fixture\"]}}"));
        });
        Check((await fake.GenerateAsync(Request, CancellationToken.None)).Text.Contains("summary.v1"), "CLI structured extraction");
        Check(CodeBuddyCliProvider.ExtractResult("[{\"type\":\"system\"},{\"type\":\"result\",\"result\":\"fixture text\"}]") == "fixture text", "CLI JSON array extraction");
        Expect<InvalidOperationException>(() => CodeBuddyCliProvider.ExtractResult("{\"is_error\":true,\"result\":\"synthetic-secret\"}"));
        foreach (int exit in new[] { 0, 1 })
        {
            var unauthorized = new CodeBuddyCliProvider(profile, (_, _, _) => Task.FromResult(
                new CliProcessResult(exit, "", "401 Authentication required. Please use /login. synthetic-private-token")));
            try { await unauthorized.GenerateAsync(Request, CancellationToken.None); throw new Exception("Authentication failure was missed."); }
            catch (ProviderFailureException ex)
            {
                Check(ex.Kind == ProviderFailureKind.CliAuthenticationRequired && ex.Message.Contains("/login"), "Actionable CLI authentication failure");
                Check(!ex.Message.Contains("synthetic-private-token"), "No CLI diagnostics in UI errors");
            }
        }
        Check(!new CliProcessResult(1, "synthetic-secret", "synthetic-secret").ToString().Contains("synthetic-secret"), "Private process result diagnostics");
        await ExpectAsync<InvalidOperationException>(() => new CodeBuddyCliProvider(profile,
            (_, _, _) => Task.FromResult(new CliProcessResult(7, "synthetic-secret"))).GenerateAsync(Request, CancellationToken.None));
        // Real child-process tests run this fixture executable, never the installed CLI.
        CodeBuddyCliProvider Child(string? mode = null) => new(profile, (start, stdin, ct) =>
        {
            start.ArgumentList.Insert(0, "--provider-child");
            if (mode is not null) start.ArgumentList.Insert(1, mode);
            return CodeBuddyCliProvider.RunProcess(start, stdin, ct);
        });
        Check((await Child().GenerateAsync(Request, CancellationToken.None)).Text.Contains("child-fixture"), "Real child stdin/result");
        using var canceled = new CancellationTokenSource(100);
        await ExpectAsync<OperationCanceledException>(() => Child("--provider-child-delay").GenerateAsync(Request, canceled.Token));
        await ExpectAsync<InvalidOperationException>(() => Child("--provider-child-oversize").GenerateAsync(Request, CancellationToken.None));
    }
    public static int RunChild(string[] args)
    {
        string input = Console.In.ReadToEnd();
        if (args.Contains("--provider-child-delay")) Thread.Sleep(60000);
        if (args.Contains("--provider-child-oversize")) { Console.Write(new string('x', 1024 * 1024 + 1)); return 0; }
        if (!input.Contains("Synthetic observed text", StringComparison.Ordinal)) return 8;
        Console.Write("{\"structured_output\":{\"schema\":\"summary.v1\",\"keyPoints\":[\"child-fixture\"]}}");
        return 0;
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("Provider probe failed: " + label); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
