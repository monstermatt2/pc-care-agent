using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace PcCareAgent;

/// <summary>Self-update: download the newest release, verify its hash, swap the exe, restart the service.</summary>
public static class Updater
{
    /// <summary>Returns true when an update is staged and the process should exit (service restarts).</summary>
    public static async Task<bool> TryUpdateAsync(ApiClient api, ILogger log, CancellationToken ct)
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        var info = await api.CheckUpdateAsync(current, ct);
        if (info is null) return false;

        log.LogInformation("Update {Version} available ({Size} bytes)", info.Version, info.Size);

        var dir = AppContext.BaseDirectory;
        var newFile = Path.Combine(dir, "PcCareAgent.new.exe");
        var curFile = Environment.ProcessPath ?? Path.Combine(dir, "PcCareAgent.exe");
        var backup = Path.Combine(dir, "PcCareAgent.old.exe");
        var bat = Path.Combine(dir, "update.bat");

        var bytes = await api.DownloadAsync(info.Url, ct);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, info.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            log.LogError("Update hash mismatch — refusing (got {Got})", hash);
            return false;
        }
        await File.WriteAllBytesAsync(newFile, bytes, ct);

        // staging cooldown: if a recent stage failed to land, back off instead of
        // crash-looping (restart -> stage -> restart -> ...) and storming toasts.
        var marker = Path.Combine(dir, "update-stage.txt");
        try
        {
            if (File.Exists(marker) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromMinutes(15))
            {
                log.LogWarning("Update staged recently but did not land — backing off 15 min");
                return false;
            }
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        }
        catch { /* marker is advisory */ }

        var svc = AgentConfig.ServiceName;
        await File.WriteAllTextAsync(bat, $"""
            @echo off
            cd /d "{dir}"
            ping -n 3 127.0.0.1 >nul
            net stop "{svc}"
            if exist "{backup}" del "{backup}"
            move /y "{curFile}" "{backup}" || (net start "{svc}" & del "{marker}" & exit /b 1)
            move /y "{newFile}" "{curFile}" || (move /y "{backup}" "{curFile}" & net start "{svc}" & del "{marker}" & exit /b 1)
            net start "{svc}"
            del "{marker}"
            del "{bat}"
            """, ct);

        log.LogInformation("Staged update to {Version}; restarting {Service} via update.bat", info.Version, svc);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
        {
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        return true; // caller must exit; update.bat waits for us to die, then swaps + restarts
    }
}
