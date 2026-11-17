using System.Security.Cryptography;
using System.Text;

namespace PcCareAgent;

/// <summary>Stable per-hardware ID: CPU + motherboard + BIOS + first-disk serials,
/// SHA-256'd. Survives OS reinstalls (no MachineGuid); changes only with hardware.
/// Used by the server to merge reinstalls onto one device row and to spot scripted
/// install floods (many "devices", one fingerprint).</summary>
public static class Fingerprint
{
    public static string Compute()
    {
        try
        {
            var (exit, stdout) = TaskRunner.RunPowerShell(
                "\"$((Get-CimInstance Win32_Processor).ProcessorId)|" +
                "$((Get-CimInstance Win32_BaseBoard).SerialNumber)|" +
                "$((Get-CimInstance Win32_BIOS).SerialNumber)|" +
                "$((Get-CimInstance Win32_DiskDrive | Select-Object -First 1).SerialNumber)\"", 30);
            if (exit != 0) return "";
            // strip ALL whitespace so trivial formatting differences don't split identities
            var raw = string.Concat((stdout ?? "").Where(c => !char.IsWhiteSpace(c)));
            if (raw.Length < 8) return "";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }
        catch
        {
            return ""; // server treats empty as "no fingerprint" and behaves as before
        }
    }
}
