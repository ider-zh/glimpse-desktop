using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Glimpse.Contracts;

namespace Glimpse.UI;

public sealed class WorkerClient : IAsyncDisposable
{
    private Process? process;
    private NamedPipeClientStream? pipe;
    private StreamWriter? writer;
    private readonly SemaphoreSlim writing = new(1);
    private readonly CancellationTokenSource stop = new();
    private Task? supervision;
    private long lastReply;
    private int disposed;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Envelope>> pending = new();
    public int? ProcessId => process is { HasExited: false } ? process.Id : null;
    public int? LastExitCode { get; private set; }
    public event Action<Envelope>? Received;
    public event Action<string>? ConnectionChanged;
    public string DataDirectory { get; } = Environment.GetEnvironmentVariable("GLIMPSE_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimpse");
    public void Start() => supervision = Task.Run(Supervise);
    private async Task Supervise()
    {
        for (int attempt = 0; attempt < 4 && !stop.IsCancellationRequested; attempt++)
        {
            try
            {
                ConnectionChanged?.Invoke(attempt == 0 ? "正在连接本地 Worker…" : $"Worker 重启 {attempt}/3；已保存内容保留，观察保持暂停。");
                string name = "glimpse-" + Guid.NewGuid().ToString("N");
                string executable = Path.Combine(AppContext.BaseDirectory, "Worker", "Glimpse.Worker.exe");
                if (!File.Exists(executable)) throw new FileNotFoundException("Worker executable missing.");
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("--pipe"); info.ArgumentList.Add(name);
                info.ArgumentList.Add("--data"); info.ArgumentList.Add(DataDirectory);
                process = Process.Start(info) ?? throw new IOException("Worker did not start.");
                pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(15000, stop.Token);
                writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                lastReply = Environment.TickCount64;
                using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var heartbeat = Heartbeat(heartbeatStop.Token);
                try
                {
                    while (await reader.ReadLineAsync(stop.Token) is { } line)
                    {
                        if (line.Length > 16 * 1024 * 1024) throw new IOException("IPC response too large.");
                        var message = JsonSerializer.Deserialize<Envelope>(line, Envelope.Json) ?? throw new IOException("Invalid IPC response.");
                        if (message.ProtocolVersion != Envelope.Version) throw new IOException("Protocol mismatch.");
                        lastReply = Environment.TickCount64;
                        if (pending.TryRemove(message.RequestId, out var completion)) completion.TrySetResult(message);
                        Received?.Invoke(message);
                    }
                }
                finally
                {
                    heartbeatStop.Cancel();
                    try { await heartbeat; } catch (OperationCanceledException) { }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { ConnectionChanged?.Invoke($"Worker 连接中断：{ex.GetType().Name}。已提交数据可恢复。"); }
            catch (OperationCanceledException) { break; }
            finally
            {
                foreach (var entry in pending)
                    if (pending.TryRemove(entry.Key, out var completion))
                        completion.TrySetException(new IOException("Worker disconnected."));
                await writing.WaitAsync(CancellationToken.None);
                try
                {
                    try { writer?.Dispose(); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    writer = null; pipe?.Dispose(); pipe = null;
                }
                finally { writing.Release(); }
                if (process is not null)
                {
                    // Closing the pipe asks the Worker to commit its closed session and
                    // release SQLite. Kill only a Worker that cannot finish within the bound.
                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await process.WaitForExitAsync(exitTimeout.Token); }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited) process.Kill(true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                    LastExitCode = process.ExitCode;
                }
                process?.Dispose(); process = null;
            }
            if (!stop.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << attempt, 8)), stop.Token);
        }
        if (!stop.IsCancellationRequested) ConnectionChanged?.Invoke("Worker 已停止（达到重启上限）。关闭并重新打开 Glimpse 后可恢复会话。");
    }
    private async Task Heartbeat(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(3000, cancellation);
            if (Environment.TickCount64 - lastReply > 12000) { pipe?.Dispose(); return; }
            await SendAsync("ping", new { }, cancellation);
        }
    }
    public async Task SendAsync<T>(string type, T payload, CancellationToken cancellation = default)
    {
        var message = Envelope.Create(type, payload);
        var completion = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.TryAdd(message.RequestId, completion);
        try
        {
            await writing.WaitAsync(cancellation);
            try
            {
                if (writer is null) throw new IOException("Worker 尚未连接。");
                await writer.WriteLineAsync(JsonSerializer.Serialize(message, Envelope.Json));
            }
            finally { writing.Release(); }
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation);
        }
        finally { pending.TryRemove(message.RequestId, out _); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); pipe?.Dispose();
        if (supervision is not null) { try { await supervision; } catch (OperationCanceledException) { } }
        writing.Dispose(); stop.Dispose();
    }
}
