using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Glimpse.Providers;

internal static class WindowsSecretProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GlimpseDesktop.AgentKeys.v1");
    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value)
    {
        byte[] bytes = Transform(Convert.FromBase64String(value), false);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob(bytes.Length, Marshal.AllocHGlobal(bytes.Length));
        var entropy = new Blob(Entropy.Length, Marshal.AllocHGlobal(Entropy.Length));
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Marshal.Copy(Entropy, 0, entropy.Data, Entropy.Length);
            bool ok = protect ? CryptProtectData(ref input, null, ref entropy, 0, 0, 1, out output) :
                CryptUnprotectData(ref input, 0, ref entropy, 0, 0, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            // DPAPI buffers are small and contain credentials on unprotect.
            if (input.Data != 0) { Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length); Marshal.FreeHGlobal(input.Data); }
            if (entropy.Data != 0) Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != 0) { Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length); LocalFree(output.Data); }
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob(int length, nint data) { public int Length = length; public nint Data = data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob data, nint description, ref Blob entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
