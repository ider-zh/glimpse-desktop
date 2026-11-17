using System.Globalization;
using System.Runtime.InteropServices;

namespace Glimpse.Worker;

/// <summary>Opening a Glimpse control must not impersonate a new captured source.</summary>
public sealed class CaptureSourceContext(int uiProcessId)
{
    private string? lastExternal;
    public void Reset(string? previous = null) => lastExternal = previous;
    public string? Resolve(nint foreground)
    {
        if (foreground == 0) return lastExternal;
        _ = GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == 0 || processId == (uint)uiProcessId) return lastExternal;
        return lastExternal = foreground.ToInt64().ToString(CultureInfo.InvariantCulture);
    }

    public static CaptureSourceContext ForCurrentWorker()
    {
        _ = int.TryParse(Environment.GetEnvironmentVariable("GLIMPSE_UI_PROCESS_ID"), out int owner);
        return new(owner);
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
