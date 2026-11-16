using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Glimpse.Windows.Probe;

internal static class ProbeCleanup
{
    public static async Task DeleteAsync(string directory)
    {
        // Only newly-created probe directories may be recursively cleaned up.
        string path = Path.GetFullPath(directory);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("glimpse-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a non-probe directory.");
        SqliteConnection.ClearAllPools();
        var elapsed = Stopwatch.StartNew();
        while (Directory.Exists(path))
        {
            try { Directory.Delete(path, true); }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                // Process signaling can precede final OS/AV handle release. A persistent
                // lock still fails the probe after this bounded retry.
                await Task.Delay(100);
            }
        }
    }
}
