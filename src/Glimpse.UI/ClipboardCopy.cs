using System.Runtime.InteropServices;
using System.Windows;

namespace Glimpse.UI;

internal static class ClipboardCopy
{
    // Clipboard owners (including remote desktop bridges) can hold a short lock.
    // Stay on the UI dispatcher/STA and retry briefly without blocking capture.
    public static async Task<bool> TrySetTextAsync(string text)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (ExternalException) when (attempt < 3) { await Task.Delay(60); }
            catch (ExternalException) { return false; }
        }
        return false;
    }
}
