using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Glimpse.Contracts;
using Glimpse.Storage;
using Glimpse.UI;

namespace Glimpse.Windows.Probe;

internal static class AgentPipeProbe
{
    public static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-agent-pipe-" + Guid.NewGuid().ToString("N"));
        var repository = new SessionRepository(Path.Combine(directory, "sessions.db"));
        var block = new ContentBlock(Guid.NewGuid(), "Early pineapple context. Contact synthetic@example.test for details.",
            "Early pineapple context.", "confirmed", new(1, 1, "Synthetic original", 1, "Synthetic original"));
        var session = new SessionData(Guid.NewGuid(), "Agent transport fixture", "paused", 1, 1,
            new(0, 0, 128, 128), 1, [block], [], []);
        await repository.SaveAsync(session);
        string? previousData = Environment.GetEnvironmentVariable("GLIMPSE_DATA_DIR");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", directory);
        var client = new WorkerClient();
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", previousData);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Received += e => { if (e.MessageType == "ready") ready.TrySetResult(); };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int calls = 0;
        AgentContextPreview? approved = null;
        var accepted = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var release = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        async Task Serve()
        {
            for (int round = 0; round < 2; round++)
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = socket.GetStream();
                var header = new List<byte>();
                while (header.Count < 32768)
                {
                    byte[] value = new byte[1]; await stream.ReadExactlyAsync(value, stop.Token); header.Add(value[0]);
                    if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                string headers = Encoding.ASCII.GetString(header.ToArray());
                Check(headers.StartsWith("POST /v1/chat/completions ", StringComparison.Ordinal), "Chat Completions route");
                string length = headers.Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1];
                int count = int.Parse(length.Trim(), System.Globalization.CultureInfo.InvariantCulture);
                Check(count is > 0 and < 256000, "Bounded request");
                var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, stop.Token);
                using var request = JsonDocument.Parse(bytes);
                var messages = request.RootElement.GetProperty("messages");
                Check(messages[0].GetProperty("content").GetString() == approved!.SystemPrompt &&
                    messages[1].GetProperty("content").GetString() == approved.UserPrompt, "Frozen preview equals outbound messages");
                Check(!approved.UserPrompt.Contains("synthetic@example.test", StringComparison.Ordinal), "Redaction before outbound request");
                Check(!request.RootElement.TryGetProperty("tools", out _) && !request.RootElement.GetRawText().Contains("image_url", StringComparison.Ordinal), "No screenshots or tools");
                Interlocked.Increment(ref calls); accepted[round].TrySetResult();
                await release[round].Task.WaitAsync(stop.Token);
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    model = "fixture-model",
                    choices = new[] { new { message = new { content = "{\"schema\":\"summary.v1\",\"keyPoints\":[\"Early pineapple context included.\"]}" } } }
                });
                byte[] responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                try { await stream.WriteAsync(responseHeader, stop.Token); await stream.WriteAsync(body, stop.Token); }
                catch (IOException) when (round == 1) { /* The canceled client may close before our response. */ }
            }
        }
        var server = Serve();
        try
        {
            client.Start(); await ready.Task.WaitAsync(stop.Token);
            var settings = (await client.RequestAsync("agent-settings-get", new { }, cancellation: stop.Token)).Read<AgentSettings>();
            var profile = settings.Providers.First(p => p.Id == "openai-compatible") with
            { BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "fixture-model", TimeoutSeconds = 20 };
            var update = new AgentProviderUpdate(profile.Id, profile);
            await client.RequestAsync("agent-settings-save", update, cancellation: stop.Token);
            await client.RequestAsync("resume", new SessionRequest(session.Id), cancellation: stop.Token);
            approved = (await client.RequestAsync("agent-preview", new AgentTaskRequest(session.Id, "summary", "Summarize pineapple"), cancellation: stop.Token)).Read<AgentContextPreview>();
            Check(calls == 0, "Preview/configuration never trigger a model request");
            var generation = client.RequestAsync("agent-run", new { previewId = approved.PreviewId }, TimeSpan.FromSeconds(20), stop.Token);
            await accepted[0].Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            var pong = await client.RequestAsync("ping", new { }, TimeSpan.FromSeconds(3), stop.Token);
            Check(pong.MessageType == "pong" && !generation.IsCompleted, "Heartbeat remains responsive during model work");
            await client.RequestAsync("pause", new { }, TimeSpan.FromSeconds(3), stop.Token);
            release[0].TrySetResult();
            var result = (await generation).Read<AgentResult>();
            Check(result.Status == "completed" && result.Cards.Single().Text == "Early pineapple context included.", "Validated copyable result");
            try { await client.RequestAsync("agent-run", new { previewId = approved.PreviewId }, cancellation: stop.Token); throw new Exception("Preview replay accepted"); }
            catch (InvalidOperationException) { }
            var stale = (await client.RequestAsync("agent-preview", new AgentTaskRequest(session.Id, "summary"), cancellation: stop.Token)).Read<AgentContextPreview>();
            await client.RequestAsync("agent-settings-save", update, cancellation: stop.Token);
            try { await client.RequestAsync("agent-run", new { previewId = stale.PreviewId }, cancellation: stop.Token); throw new Exception("Changed-engine preview accepted"); }
            catch (InvalidOperationException) { }
            approved = (await client.RequestAsync("agent-preview", new AgentTaskRequest(session.Id, "summary"), cancellation: stop.Token)).Read<AgentContextPreview>();
            var canceled = client.RequestAsync("agent-run", new { previewId = approved.PreviewId }, TimeSpan.FromSeconds(20), stop.Token);
            await accepted[1].Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            await client.RequestAsync("agent-cancel", new { previewId = approved.PreviewId }, cancellation: stop.Token);
            Check((await canceled).Read<AgentResult>().Status == "canceled", "Cancellation reaches the running provider");
            release[1].TrySetResult(); await server;
            var history = (await client.RequestAsync("agent-history", new SessionRequest(session.Id), cancellation: stop.Token)).Read<List<AgentResult>>();
            Check(history.Count == 2 && history.Any(t => t.Status == "completed") && history.Any(t => t.Status == "canceled"), "Durable Agent results");
            Console.WriteLine("AGENT IPC PASS: actual UI client/Worker/settings/preview/loopback API/card validation/SQLite history; frozen redacted payload, no pre-approval call, concurrent heartbeat/pause, stale-preview rejection and cancellation.");
        }
        finally
        {
            foreach (var gate in release) gate.TrySetResult();
            stop.Cancel(); listener.Stop();
            try { await server; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { }
            await client.DisposeAsync(); await ProbeCleanup.DeleteAsync(directory);
        }
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException("Agent pipe probe failed: " + label); }
}
