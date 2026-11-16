using System.Diagnostics;
using System.IO;
using Glimpse.Contracts;
using Glimpse.UI;

namespace Glimpse.Windows.Probe;

internal static class LoginPipeProbe
{
    public static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-login-pipe-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_DIR");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", directory);
        var client = new WorkerClient();
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", previous);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Received += e => { if (e.MessageType == "ready") ready.TrySetResult(); };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        int childId = 0;
        try
        {
            client.Start(); await ready.Task.WaitAsync(stop.Token);
            var settings = (await client.RequestAsync("agent-settings-get", new { }, cancellation: stop.Token)).Read<AgentSettings>();
            var profile = settings.Providers.First(p => p.Id == "codebuddy-cli") with
            { CliPath = Path.Combine(AppContext.BaseDirectory, "Glimpse.Windows.Probe.exe"), CliHostPath = "" };
            var update = new AgentProviderUpdate(profile.Id, profile);
            await client.RequestAsync("agent-settings-save", update, cancellation: stop.Token);
            var login = (await client.RequestAsync("agent-login-start", new { }, cancellation: stop.Token)).Read<CliLoginSnapshot>();
            await Rejected("agent-login-start", new { });
            await Rejected("agent-login-read", new CliLoginRequest(Guid.NewGuid()));
            await Rejected("agent-login-input", new CliLoginInput(Guid.NewGuid(), "x"));
            await Rejected("agent-login-resize", new CliLoginResize(Guid.NewGuid(), 90, 25));
            await Rejected("agent-settings-save", update);
            await Rejected("agent-run", new { previewId = Guid.NewGuid() });
            var snapshot = await Until(login.LoginId, s => s.ScreenText.Contains("LOGIN_FIXTURE_READY", StringComparison.Ordinal));
            Check(snapshot.Links.Contains("https://example.test/verify?code=synthetic-only"), "URL survives IPC");
            Check(snapshot.TerminalOutput.Contains("LOGIN_FIXTURE_READY", StringComparison.Ordinal), "Raw VT survives IPC");
            await client.RequestAsync("agent-login-resize", new CliLoginResize(login.LoginId, 90, 25), cancellation: stop.Token);
            await client.RequestAsync("agent-login-input", new CliLoginInput(login.LoginId, "s"), cancellation: stop.Token);
            snapshot = await Until(login.LoginId, s => s.TerminalOutput.Contains("SIZE=90x25", StringComparison.Ordinal));
            Check(snapshot.Columns == 90 && snapshot.Rows == 25, "Native resize survives IPC");
            await Rejected("agent-login-resize", new CliLoginResize(login.LoginId, 500, 25));
            var pong = await client.RequestAsync("ping", new { }, cancellation: stop.Token);
            Check(pong.MessageType == "pong", "Heartbeat while terminal is active");
            await client.RequestAsync("agent-login-input", new CliLoginInput(login.LoginId, "x"), cancellation: stop.Token);
            await Until(login.LoginId, s => s.Status == "exited");
            await client.RequestAsync("agent-login-stop", new CliLoginRequest(login.LoginId), cancellation: stop.Token);
            await Rejected("agent-login-read", new CliLoginRequest(login.LoginId));
            var again = (await client.RequestAsync("agent-login-start", new { }, cancellation: stop.Token)).Read<CliLoginSnapshot>();
            snapshot = await Until(again.LoginId, s => s.ScreenText.Contains("LOGIN_FIXTURE_READY", StringComparison.Ordinal));
            childId = int.Parse(snapshot.ScreenText.Split("PID=", StringSplitOptions.None)[1].Split('\n')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            // Dispose the actual UI client without explicitly stopping the login. Worker
            // pipe shutdown must also close its terminal and kill its owned process.
        }
        finally { await client.DisposeAsync(); await ProbeCleanup.DeleteAsync(directory); }
        if (childId != 0)
        {
            try { using var child = Process.GetProcessById(childId); Check(child.HasExited, "Disconnect kills login child"); }
            catch (ArgumentException) { }
        }
        Console.WriteLine("LOGIN IPC PASS: saved CLI profile, session IDs, heartbeat, input, login/generation exclusion, natural exit, explicit stop and pipe-close cleanup using offline terminal child.");

        async Task Rejected<T>(string type, T payload)
        {
            try { await client.RequestAsync(type, payload, cancellation: stop.Token); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Login IPC accepted invalid operation: " + type);
        }
        async Task<CliLoginSnapshot> Until(Guid id, Func<CliLoginSnapshot, bool> predicate)
        {
            for (;;)
            {
                var state = (await client.RequestAsync("agent-login-read", new CliLoginRequest(id), cancellation: stop.Token)).Read<CliLoginSnapshot>();
                if (predicate(state)) return state;
                await Task.Delay(40, stop.Token);
            }
        }
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("Login pipe probe failed: " + name); }
}
