using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Glimpse.Contracts;
using Glimpse.Providers;

namespace Glimpse.Windows.Probe;

internal static class ModelCatalogProbe
{
    public static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-model-catalog-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var profile = new AgentProviderProfile("codebuddy-cli", "Offline catalog", "codebuddy-cli", "", "", Path.Combine(AppContext.BaseDirectory, "Glimpse.Windows.Probe.exe"), "", 30, false);
        try
        {
            var start = CodeBuddyModelCatalog.CreateStartInfo(profile, directory);
            Check(start.ArgumentList.Contains("--acp") && !start.ArgumentList.Contains("--print") &&
                start.ArgumentList.Contains("--no-session-persistence") && start.ArgumentList.Contains("--strict-mcp-config"), "Metadata-only CLI arguments");
            var catalog = await CodeBuddyModelCatalog.LoadWithStartAsync(start, profile.Id, CancellationToken.None);
            Check(catalog.Models.Count == 2 && catalog.Models[0].Id == "synthetic-model-a" && catalog.CurrentModelId == "synthetic-model-a", "ACP extraction and deduplication");
            Check(catalog.Models[1].DisplayName == "Synthetic B", "Control characters removed from display name");
            var failure = CodeBuddyModelCatalog.CreateStartInfo(profile, directory); failure.ArgumentList.Add("--catalog-error");
            try { await CodeBuddyModelCatalog.LoadWithStartAsync(failure, profile.Id, CancellationToken.None); throw new Exception("Missing synthetic auth error"); }
            catch (InvalidOperationException ex) { Check(!ex.Message.Contains("synthetic-private", StringComparison.Ordinal) && ex.Message.Contains("登录", StringComparison.Ordinal), "Actionable sanitized ACP errors"); }
            var delayed = CodeBuddyModelCatalog.CreateStartInfo(profile, directory); delayed.ArgumentList.Add("--catalog-delay");
            using var cancellation = new CancellationTokenSource(200);
            try { await CodeBuddyModelCatalog.LoadWithStartAsync(delayed, profile.Id, cancellation.Token); throw new Exception("Missing cancellation"); }
            catch (OperationCanceledException) { }
            Console.WriteLine("MODEL CATALOG PASS: real synthetic ACP child, no prompt/inference, client tools denied, safe model projection/deduplication, authentication error redaction and cancellation.");
        }
        finally { await ProbeCleanup.DeleteAsync(directory); }
    }
    public static int Child(string[] args)
    {
        if (args.Contains("--catalog-delay")) { Thread.Sleep(60000); return 0; }
        using var initialize = JsonDocument.Parse(Console.ReadLine()!);
        if (initialize.RootElement.GetProperty("method").GetString() != "initialize") return 3;
        if (args.Contains("--catalog-error"))
        {
            Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32000,\"message\":\"Authentication required synthetic-private-token\"}}");
            Thread.Sleep(60000); return 0;
        }
        Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":1}}");
        using var create = JsonDocument.Parse(Console.ReadLine()!);
        if (create.RootElement.GetProperty("method").GetString() != "session/new") return 4;
        Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"fs/read_text_file\",\"params\":{\"path\":\"synthetic-private-file\"}}");
        using var denied = JsonDocument.Parse(Console.ReadLine()!);
        if (!denied.RootElement.TryGetProperty("error", out _)) return 5;
        Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"models\":{\"currentModelId\":\"synthetic-model-a\",\"availableModels\":[{\"modelId\":\"synthetic-model-a\",\"name\":\"Synthetic A\",\"apiKey\":\"never-project-this\"},{\"modelId\":\"synthetic-model-a\",\"name\":\"duplicate\"},{\"modelId\":\"synthetic-model-b\",\"name\":\"Synthetic B\\n\"},{\"modelId\":\"bad id\",\"name\":\"bad\"}]}}}");
        Thread.Sleep(60000); return 0;
    }
    public static async Task Live(string directory)
    {
        var settings = await new ProviderSettingsStore(directory).LoadPublicAsync();
        var profile = settings.Providers.Single(p => p.Id == "codebuddy-cli");
        var catalog = await CodeBuddyModelCatalog.LoadAsync(profile);
        Console.WriteLine(JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("Model catalog probe failed: " + name); }
}
