using System.Diagnostics;
using System.IO;
using System.Text;
using Glimpse.Contracts;
using Glimpse.Providers;

namespace Glimpse.Windows.Probe;

internal static class LoginTerminalProbe
{
    public static async Task Run()
    {
        var buffer = new AnsiTerminalBuffer(24, 6);
        buffer.Feed("https://example.test/verify?code=long-wrapped-synthetic-value");
        Check(buffer.Links.Contains("https://example.test/verify?code=long-wrapped-synthetic-value"), "Wrapped URL reassembled");
        buffer.Feed("\u001b]8;;javascript:alert(1)\aunsafe\u001b]8;;\a");
        Check(buffer.Links.All(link => !link.StartsWith("javascript:", StringComparison.Ordinal)), "Non-web OSC links excluded");
        for (int i = 0; i < 2000; i++) buffer.Feed("\r\nSynthetic bounded terminal history");
        Check(buffer.Text.Length < 20000, "Terminal memory bounded");
        buffer.Clear(); Check(buffer.Text.Length == 0 && buffer.Links.Count == 0, "Terminal state cleared");
        var profile = new AgentProviderProfile("codebuddy-cli", "Offline terminal fixture", "codebuddy-cli", "", "",
            Path.Combine(AppContext.BaseDirectory, "Glimpse.Windows.Probe.exe"), "", 30, false);
        var info = CodeBuddyLoginSession.CreateStartInfo(profile, Path.GetTempPath());
        Check(info.ArgumentList.Contains("/login") && !info.ArgumentList.Contains("--print") &&
            info.ArgumentList.Contains("--strict-mcp-config") && info.ArgumentList.Contains("--no-session-persistence"), "Interactive isolated login arguments");
        int childId;
        await using (var session = CodeBuddyLoginSession.Start(profile))
        {
            var first = await Until(session, s => s.ScreenText.Contains("LOGIN_FIXTURE_READY", StringComparison.Ordinal));
            Check(first.ScreenText.Contains("TTY=true", StringComparison.Ordinal), "Native child sees terminal input");
            childId = int.Parse(first.ScreenText.Split("PID=", StringSplitOptions.None)[1].Split('\n')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Check(first.Links.Contains("https://example.test/verify?code=synthetic-only"), "Split VT URL preserved");
            Check(first.TerminalOutput.Contains("\u001b[", StringComparison.Ordinal) && first.TerminalOutput.Contains("LOGIN_FIXTURE_READY", StringComparison.Ordinal), "Raw VT stream preserved");
            Check(first.Columns == 110 && first.Rows == 32 && first.OutputStart == 0, "Initial terminal dimensions and stream offset");
            Check(!first.ToString().Contains("synthetic-only", StringComparison.Ordinal), "Snapshot diagnostics hide URL");
            Check(!new CliLoginInput(session.Id, "private-input-fixture").ToString().Contains("private-input", StringComparison.Ordinal), "Input diagnostics hide text");
            await session.SendAsync("\u001b[B\r");
            await Until(session, s => s.ScreenText.Contains("KEYS_RECEIVED", StringComparison.Ordinal));
            session.Resize(90, 25);
            Check(session.Read().Columns == 90 && session.Read().Rows == 25, "Snapshot dimensions follow native resize");
            await session.SendAsync("s");
            await Until(session, s => s.TerminalOutput.Contains("SIZE=90x25", StringComparison.Ordinal));
            try { session.Resize(500, 25); throw new Exception("Unbounded resize accepted"); }
            catch (InvalidOperationException) { }
            await session.SendAsync("b");
            var bounded = await Until(session, s => s.TerminalOutput.Contains("RAW_TAIL_END", StringComparison.Ordinal));
            Check(bounded.OutputStart > 0 && bounded.TerminalOutput.Length <= 32768, "Raw VT tail and absolute offset bounded");
            Check(!bounded.ToString().Contains("RAW_TAIL_END", StringComparison.Ordinal), "Raw VT hidden from snapshot diagnostics");
        }
        await Task.Delay(100);
        try { using var child = Process.GetProcessById(childId); Check(child.HasExited, "Closing login kills actual child"); }
        catch (ArgumentException) { }
        await using (var session = CodeBuddyLoginSession.Start(profile))
        {
            await Until(session, s => s.ScreenText.Contains("LOGIN_FIXTURE_READY", StringComparison.Ordinal));
            await session.SendAsync("x");
            var completed = await Until(session, s => s.Status == "exited");
            Check(completed.ExitCode == 0, "Natural process exit reported");
        }
        Console.WriteLine("LOGIN TERMINAL PASS: actual Windows ConPTY, TTY input, ANSI/URL and raw VT stream, native resize, key forwarding, bounded offsets/memory, natural exit and child cleanup; offline synthetic executable only, no account or network.");
    }
    public static int RunChild()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Write("\u001b[2J\u001b[HPID=" + Environment.ProcessId + "\r\nTTY=" + (!Console.IsInputRedirected).ToString().ToLowerInvariant() + "\r\n");
        // A URL divided by SGR codes still needs to be selectable/copyable in full.
        Console.Write("https://example.test/verify?code=\u001b[32msynthetic-only\u001b[0m\r\nLOGIN_FIXTURE_READY\r\n");
        for (;;)
        {
            var key = Console.ReadKey(true);
            if (key.KeyChar == 'x') return 0;
            if (key.KeyChar == 's') Console.Write($"\r\nSIZE={Console.WindowWidth}x{Console.WindowHeight}\r\n");
            if (key.KeyChar == 'b') Console.Write(new string('z', 50000) + "\r\nRAW_TAIL_END\r\n");
            if (key.Key == ConsoleKey.Enter) Console.Write("\r\nKEYS_RECEIVED\r\n");
        }
    }
    private static async Task<CliLoginSnapshot> Until(CodeBuddyLoginSession session, Func<CliLoginSnapshot, bool> predicate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        for (;;)
        {
            var state = session.Read();
            if (predicate(state)) return state;
            await Task.Delay(40, stop.Token);
        }
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("Login terminal probe failed: " + name); }
}
