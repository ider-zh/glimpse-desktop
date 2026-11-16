using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Glimpse.Contracts;
using Glimpse.Storage;

namespace Glimpse.Windows.Probe;

internal static class PipeProbe
{
    public static async Task Run(string workerPath)
    {
        string data = Path.Combine(Path.GetTempPath(), "glimpse-pipe-probe-" + Guid.NewGuid());
        Guid sessionId;
        var rect = new PixelRect(-1800, 100, 650, 420);
        Directory.CreateDirectory(data);
        async Task Cycle(bool restart)
        {
            string name = "glimpse-probe-" + Guid.NewGuid().ToString("N");
            var info = new ProcessStartInfo(workerPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "--pipe", name, "--data", data }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Cannot start worker.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(timeout.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                async Task<Envelope> Receive(string type)
                {
                    while (await reader.ReadLineAsync(timeout.Token) is { } line)
                    {
                        var e = JsonSerializer.Deserialize<Envelope>(line, Envelope.Json)!;
                        if (e.MessageType == "error") throw new InvalidOperationException(e.Payload.ToString());
                        if (e.MessageType == type) return e;
                    }
                    throw new IOException("Unexpected worker EOF.");
                }
                async Task Send(Envelope message) => await writer.WriteLineAsync(JsonSerializer.Serialize(message, Envelope.Json));
                await Receive("ready");
                if (!restart)
                {
                    var create = Envelope.Create("new", new StartRequest("Safe IPC fixture", rect));
                    await Send(create);
                    var update = (await Receive("update")).Read<ReaderUpdate>();
                    sessionId = update.Session!.Id;
                    await Receive("sessions");
                    await Send(create); // Retry the same request must not create another session.
                    var repeated = (await Receive("sessions")).Read<List<SessionSummary>>();
                    if (repeated.Count != 1) throw new InvalidOperationException("Idempotency failure.");
                    await Send(Envelope.Create("ping", new { })); await Receive("pong");
                    // Crash before graceful disposal; committed session must remain valid.
                    process.Kill(true); await process.WaitForExitAsync(timeout.Token);
                }
                else
                {
                    await Send(Envelope.Create("list", new { }));
                    var saved = (await Receive("sessions")).Read<List<SessionSummary>>();
                    if (saved.Count != 1) throw new InvalidOperationException("Committed session lost after worker crash.");
                    sessionId = saved[0].Id;
                    await Send(Envelope.Create("resume", new SessionRequest(sessionId)));
                    var resumed = (await Receive("update")).Read<ReaderUpdate>();
                    if (resumed.State != "Paused" || resumed.Session!.Rect != rect)
                        throw new InvalidOperationException("Resume did not preserve geometry and paused safety state.");
                    await Receive("sessions");
                    await Send(Envelope.Create("delete", new SessionRequest(sessionId)));
                    await Receive("update");
                    if ((await Receive("sessions")).Read<List<SessionSummary>>().Count != 0)
                        throw new InvalidOperationException("Session deletion failed.");
                }
            }
            finally
            {
                await pipe.DisposeAsync();
                if (!process.HasExited)
                {
                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await process.WaitForExitAsync(exitTimeout.Token); }
                    catch (OperationCanceledException) { process.Kill(true); }
                }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        try
        {
            await Cycle(false); await Cycle(true);
            Console.WriteLine("IPC PASS: current-user named pipe, request idempotency, heartbeat, worker kill/restart, paused resume, negative rectangle persistence, cascade deletion.");
        }
        finally
        {
            await ProbeCleanup.DeleteAsync(data);
        }
    }
}
