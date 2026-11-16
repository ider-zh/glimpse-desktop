using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Glimpse.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Glimpse.Providers;

// A short-lived, user-operated terminal. Output and verification URLs are never
// persisted or sent to normal logs. This process does not receive reading context.
public sealed class CodeBuddyLoginSession : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly object consoleSync = new();
    private readonly SemaphoreSlim inputGate = new(1);
    private AnsiTerminalBuffer screen = new(110, 32);
    // UTF-16 offsets match JavaScript String.length. The tail holds at most 64KiB
    // of UTF-16 terminal data; snapshots and verification URLs remain memory-only.
    private const int MaximumTerminalCharacters = 32768;
    private readonly StringBuilder terminalOutput = new();
    private long outputStart;
    private int columns = 110, rows = 32;
    private readonly string directory;
    private readonly FileStream input;
    private readonly FileStream output;
    private readonly Task pump;
    private IntPtr console, process, job;
    private int disposed, inputStopped;
    public Guid Id { get; } = Guid.NewGuid();

    private CodeBuddyLoginSession(ProcessStartInfo start, string directory)
    {
        this.directory = directory;
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        IntPtr attributes = IntPtr.Zero, environment = IntPtr.Zero;
        bool attributesInitialized = false;
        try
        {
            Check(Native.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
            Check(Native.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));
            Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new(110, 32), inputRead, outputWrite, 0, out console));
            nuint size = 0;
            _ = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((nint)size));
            Check(Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesInitialized = true;
            Check(Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            // Explicit empty std handles let ConPTY populate console handles.
            // Without STARTF_USESTDHANDLES Windows may inherit a redirected parent
            // pipe even with bInheritHandles=false, making .NET/Node report no TTY.
            var startup = new Native.StartupInfoEx { StartupInfo = new() { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x00000100 }, AttributeList = attributes };
            var command = new StringBuilder(QuoteArgument(start.FileName));
            foreach (string arg in start.ArgumentList) command.Append(' ').Append(QuoteArgument(arg));
            string block = string.Join('\0', start.Environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Where(kv => kv.Value is not null).Select(kv => kv.Key + "=" + kv.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            Check(Native.CreateProcess(start.FileName, command, IntPtr.Zero, IntPtr.Zero, false,
                0x00080000 | 0x00000400 | 0x00000004, environment, directory, ref startup, out var info));
            process = info.Process;
            try
            {
                job = Native.CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new Win32Exception();
                var limits = new Native.JobExtendedLimits { Basic = new() { LimitFlags = 0x00002000 } };
                Check(Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.JobExtendedLimits>()));
                Check(Native.AssignProcessToJobObject(job, process));
                if (Native.ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
            }
            finally { Native.CloseHandle(info.Thread); }
            // ConPTY owns duplicated ends; retaining ours would prevent EOF.
            inputRead.Dispose(); inputRead = null;
            outputWrite.Dispose(); outputWrite = null;
            input = new FileStream(inputWrite, FileAccess.Write, 4096, false); inputWrite = null;
            output = new FileStream(outputRead, FileAccess.Read, 4096, false); outputRead = null;
            pump = Task.Run(Pump);
        }
        catch
        {
            if (job != IntPtr.Zero) Native.CloseHandle(job);
            if (process != IntPtr.Zero) { Native.TerminateProcess(process, 1); Native.CloseHandle(process); }
            // Unstarted/failed child has no output producer; closing both pipe ends
            // makes console shutdown nonblocking even on older supported Windows.
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
            if (console != IntPtr.Zero) Native.ClosePseudoConsole(console);
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    public static CodeBuddyLoginSession Start(AgentProviderProfile profile)
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { return new(CreateStartInfo(profile, directory), directory); }
        catch { DeleteEmpty(directory); throw; }
    }

    internal static ProcessStartInfo CreateStartInfo(AgentProviderProfile profile, string directory)
    {
        // Reuse installed executable/Node validation and isolated environment.
        var start = new CodeBuddyCliProvider(profile).CreateStartInfo(new("", "", "", ""), directory);
        start.ArgumentList.Clear();
        if (!string.IsNullOrWhiteSpace(profile.CliHostPath)) start.ArgumentList.Add(profile.CliPath);
        foreach (string arg in new[] { "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
            "--setting-sources", "", "--settings", "{\"disableAllHooks\":true,\"allowUntrustedFrontmatterHooks\":false}",
            "--no-session-persistence", "/login" }) start.ArgumentList.Add(arg);
        start.Environment.Remove("CODEBUDDY_FORCE_HEADLESS_BUNDLE");
        start.Environment["TERM"] = "xterm-256color";
        start.Environment.Remove("NO_COLOR");
        return start;
    }

    public CliLoginSnapshot Read()
    {
        lock (sync)
        {
            bool exited = disposed != 0 || Native.WaitForSingleObject(process, 0) == 0;
            int? code = null;
            if (disposed == 0 && exited && Native.GetExitCodeProcess(process, out uint value)) code = unchecked((int)value);
            return new(Id, exited ? "exited" : "running", screen.Text, screen.Links, code,
                terminalOutput.ToString(), outputStart, columns, rows);
        }
    }

    public void Resize(int requestedColumns, int requestedRows)
    {
        if (requestedColumns is < 20 or > 240 || requestedRows is < 5 or > 100)
            throw new InvalidOperationException("登录终端尺寸须在 20–240 列、5–100 行之间。");
        lock (consoleSync)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (console == IntPtr.Zero) throw new InvalidOperationException("登录终端已关闭。");
            // Do not hold the screen lock while resizing: ConPTY can emit output
            // during this call, and the output drain must remain available.
            Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(console, new((short)requestedColumns, (short)requestedRows)));
            lock (sync)
            {
                columns = requestedColumns; rows = requestedRows;
                screen = new(columns, rows);
            }
        }
    }

    public async Task SendAsync(string text, CancellationToken cancellation = default)
    {
        if (text.Length > 4096) throw new InvalidOperationException("单次终端输入过长。");
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (inputStopped != 0) throw new InvalidOperationException("登录终端已停止，请重新启动。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        bool entered = false;
        Task? writing = null;
        try
        {
            await inputGate.WaitAsync(deadline.Token); entered = true;
            deadline.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (inputStopped != 0) throw new InvalidOperationException("登录终端已停止，请重新启动。");
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            // Anonymous ConPTY pipes use synchronous handles. A hung write must
            // never occupy the IPC command loop indefinitely: stop the owned job
            // on deadline, which also releases its pipe handles.
            writing = Task.Run(() => { input.Write(bytes); input.Flush(); });
            await writing.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            StopProcess();
            throw new InvalidOperationException("登录终端未响应输入，已停止。请重新启动登录。");
        }
        catch (OperationCanceledException)
        {
            StopProcess(); throw;
        }
        catch (IOException)
        {
            StopProcess();
            throw new InvalidOperationException("登录终端输入失败，请重新启动登录。");
        }
        finally
        {
            if (writing is { IsCompleted: false })
                _ = writing.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (entered) inputGate.Release();
        }
    }

    private void Pump()
    {
        try
        {
            using var reader = new StreamReader(output, Encoding.UTF8, false, 4096, true);
            var buffer = new char[2048];
            while (reader.Read(buffer, 0, buffer.Length) is int count && count > 0)
            {
                lock (sync)
                {
                    _ = screen.Feed(buffer.AsSpan(0, count));
                    terminalOutput.Append(buffer, 0, count);
                    if (terminalOutput.Length > MaximumTerminalCharacters)
                    {
                        int removed = terminalOutput.Length - MaximumTerminalCharacters;
                        // Avoid starting a snapshot with half of a Unicode scalar.
                        if (char.IsLowSurrogate(terminalOutput[removed])) removed++;
                        terminalOutput.Remove(0, removed); outputStart += removed;
                    }
                }
                // The mature terminal frontend handles VT device-status queries
                // and forwards its responses over the same input channel. Do not
                // also answer here: duplicate CPR replies corrupt interactive input.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void StopProcess()
    {
        Volatile.Write(ref inputStopped, 1);
        lock (sync)
        {
            if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
            if (process != IntPtr.Zero) Native.TerminateProcess(process, 1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Kill-on-close contains all descendants, including browser launch helper
        // processes still owned by CLI. Existing browser processes are not in this job.
        StopProcess();
        // Keep draining while ClosePseudoConsole flushes terminal output.
        IntPtr closing;
        lock (consoleSync) { closing = console; console = IntPtr.Zero; }
        if (closing != IntPtr.Zero)
        {
            var close = Task.Run(() => Native.ClosePseudoConsole(closing));
            try { await close.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { output.Dispose(); }
        }
        input.Dispose();
        try { await pump.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { output.Dispose(); }
        output.Dispose();
        lock (sync)
        {
            if (process != IntPtr.Zero) { Native.CloseHandle(process); process = IntPtr.Zero; }
            screen.Clear();
            terminalOutput.Clear(); outputStart = 0;
        }
        DeleteEmpty(directory);
    }
    private static void DeleteEmpty(string path)
    {
        try { Directory.Delete(path, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(); }
    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags; public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error; }
        [StructLayout(LayoutKind.Sequential)] public struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] public struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] public struct JobBasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] public struct IoCounters { public ulong ReadOperation, WriteOperation, OtherOperation, ReadTransfer, WriteTransfer, OtherTransfer; }
        [StructLayout(LayoutKind.Sequential)] public struct JobExtendedLimits { public JobBasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
        [DllImport("kernel32.dll")] public static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] public static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll")] public static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    }
}
