using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace PcCareAgent;

public static class TaskRunner
{
    /// <summary>Runs a server-issued task. Returns (status, result) where status is done|failed.</summary>
    public static async Task<(string Status, object Result)> RunAsync(AgentTask task, ApiClient api, CancellationToken ct)
    {
        try
        {
            return task.Type switch
            {
                "cleanup_temp" => ("done", await Task.Run(() => CleanupTemp(), ct)),
                "deep_clean" => ("done", await DeepCleanAsync(api, task, ct)),
                "defender_quick_scan" => ("done", await DefenderScanAsync("QuickScan", ct)),
                "defender_full_scan" => ("done", await DefenderScanAsync("FullScan", ct)),
                "defender_enable_rtp" => ("done", await Task.Run(() => DefenderEnableRtp(), ct)),
                "service_enable_wu" => ("done", await Task.Run(() => EnableWindowsUpdate(), ct)),
                "service_start" => ("done", await Task.Run(() => StartService(task.Params), ct)),
                "health_report" => ("done", (object)SystemInfo.Snapshot(Worker.AgentVersion)),
                "rustdesk_setup" => ("done", await RustDeskAsync(api, rotateOnly: false, task, ct)),
                "rustdesk_rotate" => ("done", await RustDeskAsync(api, rotateOnly: true, task, ct)),
                _ => ("failed", (object)new { error = $"unknown task type: {task.Type}" }),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ("failed", new { error = ex.Message });
        }
    }

    /// <summary>Query Defender status without running a scan (used each few cycles).</summary>
    public static DefenderFacts? QueryDefender()
    {
        var (exit, json) = RunExe("powershell.exe",
            "-NoProfile -Command \"Get-MpComputerStatus | Select-Object RealTimeProtectionEnabled,AMServiceEnabled,AntivirusSignatureLastUpdated | ConvertTo-Json -Compress\"", 60);
        return exit == 0 ? DefenderFacts.Parse(json) : null;
    }

    /// <summary>Re-enable Defender real-time protection (the AI fix for "RTP is OFF").</summary>
    private static object DefenderEnableRtp()
    {
        var (exit, _) = RunExe("powershell.exe",
            "-NoProfile -Command \"Set-MpPreference -DisableRealtimeMonitoring $false\"", 90);
        Thread.Sleep(1500);
        var f = QueryDefender();
        return new
        {
            requested = exit == 0,
            realtimeNow = f?.RealTimeProtectionEnabled,
            amServiceNow = f?.AMServiceEnabled,
        };
    }

    /// <summary>Run a PowerShell script with ZERO quoting issues: base64-encoded command.</summary>
    internal static (int exitCode, string stdout) RunPowerShell(string script, int timeoutSec)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return RunExe("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {b64}", timeoutSec);
    }

    /// <summary>Query critical services (currently: Windows Update).
    /// NOTE: uses CIM — PS 5.1's Get-Service has no StartType property.</summary>
    public static ServiceFacts? QueryServices()
    {
        var (exit, json) = RunPowerShell(
            "Get-CimInstance Win32_Service -Filter \"Name='wuauserv'\" | Select-Object State,StartMode | ConvertTo-Json -Compress", 45);
        return exit == 0 ? ServiceFacts.Parse(json) : null;
    }

    /// <summary>Re-enable + start Windows Update (the AI fix for "updates disabled").</summary>
    private static object EnableWindowsUpdate()
    {
        var (exit, _) = RunPowerShell(
            "Set-Service wuauserv -StartupType Manual; Start-Service wuauserv -ErrorAction SilentlyContinue", 90);
        Thread.Sleep(1500);
        var f = QueryServices();
        return new
        {
            requested = exit == 0,
            wuEnabledNow = f?.WuEnabled,
            wuRunningNow = f?.WuRunning,
        };
    }

    /// <summary>Services the AI may (re)start — deliberately small, agent-side allowlist.</summary>
    private static readonly HashSet<string> StartableServices = new(StringComparer.OrdinalIgnoreCase)
        { "spooler", "wuauserv", "dhcp", "dnscache", "w32time", "audiosrv", "bits", "wsearch" };

    /// <summary>Generic AI fix: (re)enable + start an allowlisted Windows service.</summary>
    private static object StartService(Dictionary<string, System.Text.Json.JsonElement>? p)
    {
        var svc = p is not null && p.TryGetValue("service", out var s)
                  && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString()! : null;
        if (svc is null || !StartableServices.Contains(svc))
            return new { error = "unsupported service", service = svc };
        var startup = svc.Equals("wuauserv", StringComparison.OrdinalIgnoreCase) ? "Manual" : "Automatic";
        var (exit, _) = RunPowerShell(
            $"Set-Service {svc} -StartupType {startup}; Start-Service {svc} -ErrorAction SilentlyContinue", 90);
        Thread.Sleep(1500);
        var (e2, j2) = RunPowerShell(
            $"Get-CimInstance Win32_Service -Filter \"Name='{svc}'\" | Select-Object State,StartMode | ConvertTo-Json -Compress", 45);
        var f = e2 == 0 ? ServiceFacts.Parse(j2) : null;
        return new
        {
            requested = exit == 0,
            service = svc,
            runningNow = f?.WuRunning,
            enabledNow = f?.WuEnabled,
        };
    }

    /// <summary>General "look around" evidence pack for the AI brain — lets GLM spot issues
    /// nobody wrote a detector for: stopped auto-services, 24h error bursts, pending reboots.</summary>
    public static List<string> QuerySurvey()
    {
        const string script = @"
$out = @()
# services: auto-but-stopped OR key user-visible services that are not running
# (a DISABLED Spooler/Audiosrv/WSearch is the #1 real-world breakage - StartMode
#  'Disabled' hides from a plain Auto filter, so key services are named explicitly)
Get-CimInstance Win32_Service -Filter ""(StartMode='Auto' AND State!='Running') OR Name='spooler' OR Name='audiosrv' OR Name='wsearch' OR Name='dnscache' OR Name='dhcp' OR Name='w32time' OR Name='bits'"" |
  Where-Object { $_.State -ne 'Running' -and $_.Name -ne 'wuauserv' -and $_.Name -notin @('RemoteRegistry','TrustedInstaller','sppsvc','Dosvc','SEMgrSvc','WalletService','PhoneSvc','RemoteAccess','ClipSVC','BITS') } |
  Select-Object -First 6 | ForEach-Object { $out += ""service not running: $($_.Name) ($($_.DisplayName)) [$($_.StartMode)]"" }
# devices with driver problems (broken audio/GPU/network after updates)
Get-CimInstance Win32_PnPEntity -Filter ""ConfigManagerErrorCode<>0"" -ErrorAction SilentlyContinue |
  Select-Object -First 3 | ForEach-Object { $out += ""device problem: $($_.Name) [code $($_.ConfigManagerErrorCode)]"" }
# memory pressure
$os = Get-CimInstance Win32_OperatingSystem
if ($os.FreePhysicalMemory * 10 -lt $os.TotalVisibleMemorySize) { $out += ""low-memory: $([math]::Round($os.FreePhysicalMemory/1MB,1)) GB free of $([math]::Round($os.TotalVisibleMemorySize/1MB,1)) GB"" }
# disk space
Get-CimInstance Win32_LogicalDisk -Filter ""DriveType=3"" | Where-Object { $_.FreeSpace * 10 -lt $_.Size } |
  ForEach-Object { $out += ""disk-low: $($_.DeviceID) $([math]::Round($_.FreeSpace/1GB,1)) GB free of $([math]::Round($_.Size/1GB,1)) GB"" }
# blue screens (7d)
$bc = @(Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WER-SystemErrorReporting'; StartTime=(Get-Date).AddDays(-7)} -ErrorAction SilentlyContinue)
if ($bc.Count -gt 0) { $out += ""bsod-last-7d: $($bc.Count) bugcheck(s)"" }
# app crashes (7d)
$cr = @(Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1002; StartTime=(Get-Date).AddDays(-7)} -ErrorAction SilentlyContinue)
if ($cr.Count -gt 5) { $out += ""app-crashes-7d: $($cr.Count)"" }
# startup bloat
$suc = @(Get-CimInstance Win32_StartupCommand).Count
if ($suc -ge 20) { $out += ""startup-entries: $suc (bloat)"" }
# error bursts (24h, by source)
Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2; StartTime=(Get-Date).AddHours(-24)} -ErrorAction SilentlyContinue |
  Group-Object -Property ProviderName | Sort-Object Count -Descending | Select-Object -First 4 |
  ForEach-Object { $out += ""error-burst 24h: $($_.Count)x $($_.Name) (e.g. event $($_.Group[0].Id))"" }
# pending reboots
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $out += 'reboot-pending: windows component servicing' }
$pfro = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations
if ($pfro) { $out += 'reboot-pending: file rename operations queued' }
$out | Select-Object -First 18
";
        var (exit, stdout) = RunPowerShell(script, 60);
        var lines = new List<string>();
        if (exit != 0) return lines;
        foreach (var ln in stdout.Split('\n'))
        {
            var t = ln.Trim('\r', ' ');
            if (t.Length > 0 && lines.Count < 18) lines.Add(t);
        }
        return lines;
    }

    // ---------- Deep Clean (BleachBit-inspired safe set) ----------
    // Locations are public-knowledge cache/temp paths (same coverage class as open-source
    // cleaners). Deliberately EXCLUDED: Prefetch (slows app launches), registry MRU/MUICache
    // (risk with no real benefit), browser history/cookies/passwords (privacy — never touch).
    private sealed record CleanTarget(string Id, string Name, string BaseDir, string Pattern);

    /// <summary>Selected target ids from task params, or null = clean everything.</summary>
    private static HashSet<string>? ParseTargetIds(AgentTask task)
    {
        if (task.Params is null || !task.Params.TryGetValue("targets", out var el)) return null;
        if (el.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var s = new HashSet<string>();
        foreach (var x in el.EnumerateArray())
            if (x.ValueKind == System.Text.Json.JsonValueKind.String) s.Add(x.GetString()!);
        return s.Count > 0 ? s : null;
    }

    private static async Task<object> DeepCleanAsync(ApiClient api, AgentTask task, CancellationToken ct)
    {
        var selected = ParseTargetIds(task);
        bool Want(string id) => selected is null || selected.Contains(id);

        var targets = new List<CleanTarget>();
        void Add(string id, string name, string dir, string pat = "*")
        { if (Want(id) && Directory.Exists(dir)) targets.Add(new CleanTarget(id, name, dir, pat)); }

        Add("windows_temp", "Windows temp files", @"C:\Windows\Temp");
        Add("update_cache", "Windows Update cache", @"C:\Windows\SoftwareDistribution\Download");
        Add("error_reports", "Error reports", @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue");
        Add("error_reports", "Error reports", @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive");
        Add("dumps", "Memory dumps", @"C:\Windows\Minidump");
        if (Want("dumps") && File.Exists(@"C:\Windows\MEMORY.DMP"))
            targets.Add(new CleanTarget("dumps", "Memory dumps", @"C:\Windows", "MEMORY.DMP"));
        Add("delivery_opt", "Delivery Optimization cache",
            @"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");

        foreach (var u in Directory.GetDirectories(@"C:\Users"))
        {
            var local = Path.Combine(u, "AppData", "Local");
            Add("user_temp", "User temp files", Path.Combine(local, "Temp"));
            Add("thumbnails", "Thumbnail cache", Path.Combine(local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db");
            Add("shader_cache", "Shader cache", Path.Combine(local, "D3DSCache"));
            Add("crash_dumps", "Crash dumps", Path.Combine(local, "CrashDumps"));

            foreach (var (id, label, browser) in new[]
                     {
                         ("chrome_cache", "Chrome", @"Google\Chrome\User Data"),
                         ("edge_cache", "Edge", @"Microsoft\Edge\User Data"),
                     })
            {
                var ud = Path.Combine(local, browser);
                if (!Want(id) || !Directory.Exists(ud)) continue;
                foreach (var profile in Directory.EnumerateDirectories(ud, "*", SearchOption.TopDirectoryOnly))
                    foreach (var sub in new[] { "Cache", "Cache_Data", "Code Cache", "GPUCache" })
                        targets.Add(new CleanTarget(id, $"{label} cache", Path.Combine(profile, sub), "*"));
            }
            var ff = Path.Combine(local, @"Mozilla\Firefox\Profiles");
            if (Want("firefox_cache") && Directory.Exists(ff))
                foreach (var profile in Directory.EnumerateDirectories(ff, "*", SearchOption.TopDirectoryOnly))
                    targets.Add(new CleanTarget("firefox_cache", "Firefox cache", Path.Combine(profile, "cache2"), "*"));
        }

        // pseudo-targets handled via PowerShell after the file pass
        if (Want("recycle_bin")) targets.Add(new CleanTarget("recycle_bin", "Recycle Bin", "", ""));
        if (Want("dns_flush")) targets.Add(new CleanTarget("dns_flush", "DNS cache", "", ""));

        long freed = 0, items = 0, locked = 0;
        var details = new List<object>();
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var pct = (int)(100.0 * i / targets.Count);
            try { await api.ReportProgressAsync(task.Id, pct, t.Name, freed / 1048576.0, items, ct); }
            catch { /* progress is best-effort */ }

            if (t.Id == "recycle_bin" || t.Id == "dns_flush")
            {
                var ps = t.Id == "recycle_bin"
                    ? "-NoProfile -Command \"Clear-RecycleBin -Force -ErrorAction SilentlyContinue\""
                    : "-NoProfile -Command \"ipconfig /flushdns | Out-Null\"";
                var (exit, _) = RunExe("powershell.exe", ps, 90);
                if (exit != 0) locked++;
                continue;
            }

            long tFreed = 0, tItems = 0, tLocked = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(t.BaseDir, t.Pattern, SearchOption.AllDirectories))
                {
                    try
                    {
                        var len = new FileInfo(f).Length;
                        File.Delete(f);
                        tFreed += len; tItems++;
                    }
                    catch { tLocked++; }
                }
            }
            catch { /* target unavailable */ }
            if (tFreed > 0 || tLocked > 0)
                details.Add(new { name = t.Name, files = tItems, locked = tLocked, mb = Math.Round(tFreed / 1048576.0, 1) });
            freed += tFreed; items += tItems; locked += tLocked;
        }

        try { await api.ReportProgressAsync(task.Id, 100, "Finishing", freed / 1048576.0, items, ct); }
        catch { /* progress is best-effort */ }

        return new
        {
            freedMb = Math.Round(freed / 1048576.0, 1),
            items,
            locked,
            locations = details,
        };
    }

    /// <summary>Best-effort cleanup of user + Windows temp directories.</summary>
    private static object CleanupTemp()
    {
        long freedBytes = 0;
        int deletedFiles = 0;
        int skipped = 0;

        var dirs = new List<string> { @"C:\Windows\Temp" };
        try { dirs.Add(Path.GetTempPath()); } catch { }

        foreach (var dir in dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                try
                {
                    if (File.Exists(entry))
                    {
                        var len = new FileInfo(entry).Length;
                        File.Delete(entry);
                        freedBytes += len;
                        deletedFiles++;
                    }
                    else
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                }
                catch { skipped++; } // in use / access denied — completely normal for temp dirs
            }
        }
        return new { filesDeleted = deletedFiles, bytesFreed = freedBytes, skippedInUse = skipped };
    }

    /// <summary>Runs a Defender scan and returns its exit code plus current protection status.</summary>
    private static async Task<DefenderScanOutcome> DefenderScanAsync(string scanType, CancellationToken ct)
    {
        var scan = await RunPowerShellAsync($"Start-MpScan -ScanType {scanType}", ct);
        var status = await RunPowerShellAsync(
            "Get-MpComputerStatus | Select-Object AMServiceEnabled,RealTimeProtectionEnabled," +
            "AntivirusSignatureLastUpdated,AntispywareSignatureLastUpdated | ConvertTo-Json -Compress",
            ct);

        return new DefenderScanOutcome(scanType, scan.exitCode, status.stdout.Trim());
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunPowerShellAsync(
        string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start powershell.exe (is this Windows?)");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdout, await stderr);
    }

    // ---------- RustDesk remote support (AnyDesk-style ID + rotating password) ----------

    internal static (int exitCode, string stdout) RunExe(string exe, string args, int timeoutSec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        // read pipes ASYNC-first or a full pipe deadlocks us before the timeout can fire
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(timeoutSec)))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            p.WaitForExit(5000);
            return (-1, stdoutTask.IsCompleted ? stdoutTask.Result : "");
        }
        // RACE FIX: WaitForExit(timeout) can return before the async pipe read completes —
        // give the readers a moment to flush, or fast commands intermittently read as empty.
        try { stdoutTask.Wait(5000); } catch { /* ignored */ }
        return (p.ExitCode, stdoutTask.IsCompleted ? stdoutTask.Result : "");
    }

    private static bool WindowsServiceExists(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return k is not null;
    }

    private static string RandomPassword(int length)
    {
        // no ambiguous chars (0/O, 1/l/I) — these get typed by hand in the RustDesk viewer
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    private static string? RustDeskExe()
    {
        var rd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
        return File.Exists(rd) ? rd : null;
    }

    /// <summary>The RustDesk installer embedded in this exe (single-file distribution —
    /// clients never download it from the internet; falls back to the API mirror).</summary>
    private static byte[]? EmbeddedRustDeskInstaller()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PcCareAgent.resources.rustdesk-setup.exe");
            if (s is null || s.Length < 1_000_000) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>Installs/updates RustDesk as an unattended service on our self-hosted server,
    /// sets a fresh random password, and reports ID+password back to the tech's API.</summary>
    private static async Task<object> RustDeskAsync(ApiClient api, bool rotateOnly, AgentTask task, CancellationToken ct)
    {
        var host = task.Params is not null && task.Params.TryGetValue("serverHost", out var h)
                   && h.ValueKind == System.Text.Json.JsonValueKind.String && h.GetString()?.Length > 0
            ? h.GetString()! : "api.monstermatt2.net";
        var key = task.Params is not null && task.Params.TryGetValue("key", out var k)
                  && k.ValueKind == System.Text.Json.JsonValueKind.String ? k.GetString()! : "";

        var rd = RustDeskExe();
        if (rd is null)
        {
            if (rotateOnly) return new { error = "RustDesk not installed — run rustdesk_setup first" };

            var toolDir = Path.Combine(AgentConfig.ConfigDir, "tools");
            Directory.CreateDirectory(toolDir);
            var installer = Path.Combine(toolDir, "rustdesk-setup.exe");
            var bytes = EmbeddedRustDeskInstaller()
                ?? await api.DownloadAsync("api/v1/download/tool/rustdesk.exe", ct);
            await File.WriteAllBytesAsync(installer, bytes, ct);

            var (installExit, _) = RunExe(installer, "--silent-install", 240);
            for (var i = 0; i < 45 && RustDeskExe() is null; i++) await Task.Delay(2000, ct);
            rd = RustDeskExe()
                ?? throw new InvalidOperationException($"silent-install failed (exit {installExit}) — rustdesk.exe not found");
        }

        // unattended access needs the service
        if (!WindowsServiceExists("RustDesk"))
        {
            var (svcExit, _) = RunExe(rd, "--install-service", 120);
            for (var i = 0; i < 30 && !WindowsServiceExists("RustDesk"); i++) await Task.Delay(2000, ct);
            if (!WindowsServiceExists("RustDesk"))
                throw new InvalidOperationException($"--install-service failed (exit {svcExit})");
        }
        RunExe("cmd.exe", "/c net start RustDesk 2>nul & exit 0", 30); // best-effort start

        if (!rotateOnly)
        {
            // point the RustDesk service at our self-hosted rendezvous/relay
            var rdConfigDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "config", "systemprofile", "AppData", "Roaming", "RustDesk", "config");
            Directory.CreateDirectory(rdConfigDir);
            var toml = $"custom-rendezvous-server = 1\n" +
                       $"rendezvous-server = '{host}'\n" +
                       $"relay-server = '{host}:21117'\n" +
                       $"key = '{key}'\n";
            File.WriteAllText(Path.Combine(rdConfigDir, "RustDesk2.toml"), toml);
            RunExe("cmd.exe", "/c net stop RustDesk 2>nul & net start RustDesk", 120); // reload config
        }

        var password = RandomPassword(10);
        var (pwExit, _) = RunExe(rd, $"--password {password}", 60);
        var (idExit, idOut) = RunExe(rd, "--get-id", 60);
        var rustdeskId = new string((idOut ?? "").Where(char.IsDigit).Take(16).ToArray());
        if (rustdeskId.Length < 4) throw new InvalidOperationException($"--get-id failed (exit {idExit}, out '{(idOut ?? "").Trim()}')");

        await api.ReportRemoteAsync(rustdeskId, password, ct);
        return new { rustdeskId, rotateOnly, note = "credentials reported to server" };
    }
}
