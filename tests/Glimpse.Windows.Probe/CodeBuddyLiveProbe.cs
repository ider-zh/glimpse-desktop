using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Glimpse.Agent;
using Glimpse.Contracts;
using Glimpse.Providers;
using Glimpse.Storage;

namespace Glimpse.Windows.Probe;

internal static class CodeBuddyLiveProbe
{
    public static async Task Diagnose(string directory, bool userSettings = false)
    {
        var repository = new SessionRepository(Path.Combine(directory, "sessions.db"));
        var session = await repository.LoadAsync((await repository.ListAsync()).Single().Id) ?? throw new InvalidOperationException();
        var settings = await new ProviderSettingsStore(directory).LoadPublicAsync();
        var profile = settings.Providers.Single(p => p.Id == "codebuddy-cli");
        var preview = new SessionContextBuilder().Build(session, new(session.Id, "summary"));
        var request = new ProviderRequest(preview.SystemPrompt, preview.UserPrompt, preview.Schema, preview.JsonSchema);
        string work = Path.Combine(Path.GetTempPath(), "glimpse-live-diagnostic-" + Guid.NewGuid());
        Directory.CreateDirectory(work);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var process = new Process { StartInfo = new CodeBuddyCliProvider(profile).CreateStartInfo(request, work) };
        bool started = false;
        if (userSettings)
        {
            int source = process.StartInfo.ArgumentList.IndexOf("--setting-sources");
            process.StartInfo.ArgumentList[source + 1] = "user";
        }
        try
        {
            process.Start();
            started = true;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(request.UserPrompt); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout, error = await stderr;
            var fields = new Dictionary<string, object?> { ["exitCode"] = process.ExitCode,
                ["settingsSource"] = userSettings ? "user" : "none",
                ["stdoutCharacters"] = output.Length, ["stderrCharacters"] = error.Length };
            try
            {
                using var json = JsonDocument.Parse(output);
                fields["validJson"] = true;
            }
            catch (JsonException) { fields["validJson"] = false; }
            fields["authenticationRequired"] = CodeBuddyCliProvider.AuthenticationRequired(error);
            Console.WriteLine(JsonSerializer.Serialize(fields, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { Directory.Delete(work, false); } catch (IOException) { }
        }
    }
    public static async Task PrepareCopy(string directory)
    {
        await Prepare(directory);
        var repository = new SessionRepository(Path.Combine(Path.GetFullPath(directory), "sessions.db"));
        var session = await repository.LoadAsync((await repository.ListAsync()).Single().Id) ?? throw new InvalidOperationException();
        await repository.SaveAgentResultAsync(new(Guid.NewGuid(), session.Id, session.Revision, "offline-copy-fixture",
            "summary", "summary.v1", "completed", [new("离线复制测试（非 CodeBuddy 生成）", "合成复制校验：青松读书会 · 周六 10:00\n带一本书和一个讨论问题。")],
            null, null, 0, ["Offline fixture only; this does not verify authenticated CLI generation."]), null);
        Console.WriteLine("Prepared a clearly labeled offline card for manual UI clipboard verification only.");
    }
    // Opt-in fixture preparation only. Inference is triggered later through the real UI.
    public static async Task Prepare(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new InvalidOperationException("Live fixture directory must be new.");
        var repository = new SessionRepository(Path.Combine(directory, "sessions.db"));
        var lines = new[]
        {
            "合成阅读测试：青松读书会计划在周六上午十点讨论《小王子》。",
            "地点是虚构的星光图书馆，参加者需要带一本书和一个讨论问题。",
            "本次活动无费用；组织者希望讨论友谊、责任和想象力。",
            "隐私过滤测试：联系邮箱 synthetic@example.test，手机号 13800138000；这些都是测试占位符。"
        };
        var blocks = lines.Select((line, index) => new ContentBlock(Guid.NewGuid(), line, line, "confirmed",
            new(1, index + 1, line, 1, line))).ToList();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await repository.SaveAsync(new(Guid.NewGuid(), "CodeBuddy CLI 真实流程 · 合成内容", "paused", now, now,
            new(100, 100, 600, 400), 4, blocks, [], []));
        var store = new ProviderSettingsStore(directory);
        var defaults = await store.LoadPublicAsync();
        var cli = defaults.Providers.First(p => p.Id == "codebuddy-cli");
        if (!File.Exists(cli.CliPath)) throw new InvalidOperationException("Installed CodeBuddy CLI was not detected.");
        await store.SaveAsync(new(cli.Id, cli with { TimeoutSeconds = 120 }));
        Console.WriteLine("Prepared a paused synthetic session and detected CodeBuddy CLI profile. No inference or credential reads were performed by the fixture.");
    }
}
