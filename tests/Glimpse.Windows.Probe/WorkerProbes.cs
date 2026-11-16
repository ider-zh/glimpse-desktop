using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using Glimpse.Contracts;
using Glimpse.Core;
using Glimpse.Storage;
using Glimpse.UI;
using Glimpse.Worker;
using Microsoft.Data.Sqlite;

namespace Glimpse.Windows.Probe;

internal static class WorkerProbes
{
    public static async Task Run()
    {
        await CancellationProbe();
        await SupervisorProbe();
    }
    private static async Task CancellationProbe()
    {
        string data = Path.Combine(Path.GetTempPath(), "glimpse-cancel-" + Guid.NewGuid());
        var repository = new SessionRepository(Path.Combine(data, "sessions.db"));
        var fake = new DelayedOcr();
        var updates = new List<ReaderUpdate>();
        var service = new ReaderService(repository, update => { lock (updates) updates.Add(update); return Task.CompletedTask; },
            screenCapture: new FakeCapture(), ocrEngine: fake);
        using var stop = new CancellationTokenSource();
        var loop = service.RunAsync(stop.Token);
        SessionData Session() => new(Guid.NewGuid(), "Cancellation fixture", "paused", 1, 1, new(0, 0, 128, 128), 0, [], [], []);
        var first = Session(); var second = Session();
        try
        {
            await service.SetSessionAsync(first, true, stop.Token);
            await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.SetSessionAsync(second, false, stop.Token);
            fake.Finish.SetResult();
            await Task.Delay(300);
            if ((await repository.LoadAsync(first.Id))!.Revision != 0 ||
                (await repository.LoadAsync(second.Id))!.Revision != 0 || updates.Any(u => u.State == "Recorded"))
                throw new InvalidOperationException("Stale OCR result was allowed to commit after session switch.");
            Console.WriteLine("CANCELLATION PASS: delayed fake OCR ignores cancellation; generation still prevents stale commit and green state.");
        }
        finally
        {
            fake.Finish.TrySetResult(); stop.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
            await service.DisposeAsync();
            SqliteConnection.ClearAllPools(); Directory.Delete(data, true);
        }
    }
    private static async Task SupervisorProbe()
    {
        string data = Path.Combine(Path.GetTempPath(), "glimpse-supervisor-" + Guid.NewGuid());
        string? previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_DIR");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", data);
        var client = new WorkerClient();
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", previous);
        var messages = Channel.CreateUnbounded<Envelope>();
        client.Received += e => messages.Writer.TryWrite(e);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        async Task<Envelope> Receive(string type)
        {
            while (true)
            {
                var e = await messages.Reader.ReadAsync(timeout.Token);
                if (e.MessageType == "error") throw new InvalidOperationException("Worker returned error during supervision probe.");
                if (e.MessageType == type) return e;
            }
        }
        try
        {
            client.Start(); await Receive("ready");
            await client.SendAsync("new", new StartRequest("Supervisor fixture", new(0, 0, 128, 128)));
            var first = (await Receive("update")).Read<ReaderUpdate>();
            Guid id = first.Session!.Id;
            int pid = client.ProcessId ?? throw new InvalidOperationException("No supervised worker PID.");
            using (var process = Process.GetProcessById(pid)) { process.Kill(true); await process.WaitForExitAsync(timeout.Token); }
            await Receive("ready");
            if (client.ProcessId == pid) throw new InvalidOperationException("Worker was not restarted.");
            await client.SendAsync("resume", new SessionRequest(id));
            var restored = (await Receive("update")).Read<ReaderUpdate>();
            if (restored.Session?.Id != id || restored.State != "Paused") throw new InvalidOperationException("Supervised restore failed.");
            Console.WriteLine("SUPERVISION PASS: UI client recovered from owned worker kill, reconnected, loaded the committed session paused.");
        }
        finally
        {
            await client.DisposeAsync(); SqliteConnection.ClearAllPools();
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }
    private sealed class FakeCapture : IScreenCapture
    {
        public byte[] Capture(PixelRect rect) => new byte[rect.Width * rect.Height * 4];
    }
    private sealed class DelayedOcr : IOcrEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "Delayed Fake OCR";
        public async Task<List<OcrBlock>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellation)
        {
            Started.TrySetResult(); await Finish.Task;
            return [new("This obsolete OCR result must never be committed.", new(0, 0, 120, 20))];
        }
    }
}
