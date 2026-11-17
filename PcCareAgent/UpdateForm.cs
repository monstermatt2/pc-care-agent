using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;

namespace PcCareAgent;

/// <summary>The launcher update flow: check → (download → install → relaunch) → main app.
/// Shown at every app start (desktop click) and again when the user taps the
/// flashing "New update" button. The relaunch loop is the classic updater pattern:
/// after an install the NEW process opens on this same screen, finds nothing, and
/// continues into the main program.</summary>
public sealed class UpdateForm : Form
{
    public enum Outcome { None, OpenMain, AppliedRelaunching }

    /// <summary>What the launcher should do after this window closed.</summary>
    public static Outcome Result { get; set; } = Outcome.None;

    private static readonly Color Navy = Color.FromArgb(15, 34, 79);
    private static readonly Color Orange = Color.FromArgb(245, 158, 11);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly ApiClient _api;
    private readonly Label _status = new();
    private readonly Label _sub = new();
    private readonly ProgressBar _bar = new();

    public UpdateForm()
    {
        Text = "Matt's PC Repair — PC Care";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(430, 190);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.5F);

        try
        {
            using var s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PcCareAgent.resources.logo128.png");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                Icon = Brand.LogoIcon() ?? Icon;
            }
        }
        catch { /* logo optional */ }

        _status.Text = "Checking for updates…";
        _status.Font = new Font("Segoe UI", 12.5F, FontStyle.Bold);
        _status.ForeColor = Navy;
        _status.Location = new Point(28, 34);
        _status.AutoSize = true;
        Controls.Add(_status);

        _sub.Text = "";
        _sub.Font = new Font("Segoe UI", 9F);
        _sub.ForeColor = Color.Gray;
        _sub.Location = new Point(30, 70);
        _sub.AutoSize = true;
        Controls.Add(_sub);

        _bar.Style = ProgressBarStyle.Marquee;
        _bar.Location = new Point(30, 110);
        _bar.Size = new Size(370, 18);
        Controls.Add(_bar);

        _api = new ApiClient(_http, AgentConfig.Load());
        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, _) => { if (Result == Outcome.None) Result = Outcome.OpenMain; };
    }

    private void Set(string status, string sub = "", bool marquee = true, int pct = 0)
    {
        _status.Text = status;
        _sub.Text = sub;
        _bar.Style = marquee ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (!marquee) _bar.Value = Math.Clamp(pct, 0, 100);
    }

    private async Task RunAsync()
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        UpdateInfo? info = null;
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                info = await _api.CheckUpdateAsync(current, cts.Token);
        }
        catch { /* offline / server down -> fall through as "no info" */ }
        await Task.Delay(650); // let the check screen be seen, not a flash

        if (info is null)
        {
            Set("You're up to date", $"Version {current}", marquee: false, pct: 100);
            await Task.Delay(900);
            Result = Outcome.OpenMain;
            Close();
            return;
        }

        // ---- download with a real progress bar ----
        var tmp = Path.Combine(Path.GetTempPath(), $"pcare-{info.Version}.exe");
        Set($"Downloading version {info.Version}…", "0 MB", marquee: false, pct: 0);
        try
        {
            await DownloadToFileAsync(info, tmp);
        }
        catch
        {
            await FailAsync($"Couldn't download the update — continuing on {current}");
            return;
        }

        // ---- verify before installing ----
        try
        {
            await using var fs = File.OpenRead(tmp);
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(fs)).ToLowerInvariant();
            if (hash != info.Sha256.ToLowerInvariant())
            {
                File.Delete(tmp);
                await FailAsync("Download failed verification — continuing on " + current);
                return;
            }
        }
        catch
        {
            await FailAsync("Couldn't verify the update — continuing on " + current);
            return;
        }

        // ---- install: swap via update.bat (stops/starts the service too) ----
        Set("Installing update…", "The app will restart in a moment");
        if (Swap.TryStageAndLaunch(tmp))
        {
            Result = Outcome.AppliedRelaunching;   // this process exits; bat relaunches the NEW exe on this same screen
            Close();
            return;
        }
        if (Swap.NeedsElevation)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--apply-update \"{tmp}\"")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                });
                Result = Outcome.AppliedRelaunching;
                Close();
                return;
            }
            catch { /* user declined UAC -> fall through */ }
        }
        await FailAsync("Couldn't install the update — continuing on " + current);
    }

    private async Task FailAsync(string message)
    {
        Set(message, "", marquee: false, pct: 100);
        _status.ForeColor = Color.Firebrick;
        await Task.Delay(2200);
        Result = Outcome.OpenMain;
        Close();
    }

    private async Task DownloadToFileAsync(UpdateInfo info, string dest)
    {
        using var resp = await _http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? info.Size;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        var buf = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n));
            read += n;
            if (total > 0 && !IsDisposed)
                BeginInvoke(() =>
                {
                    _bar.Value = (int)Math.Min(100, read * 100 / total);
                    _sub.Text = $"{read / 1048576.0:F1} / {total / 1048576.0:F1} MB";
                });
        }
    }
}

/// <summary>The exe swap: stage the downloaded file, write update.bat (waits for us to
/// die, stops the service, swaps exes, starts the service, relaunches the app
/// non-elevated), and fire it. Mirrors the service's own Updater, plus the UI relaunch.</summary>
public static class Swap
{
    public static bool NeedsElevation { get; private set; }

    /// <summary>Stage + launch the swap. True when the bat is running (caller must exit).</summary>
    public static bool TryStageAndLaunch(string downloadedExe)
    {
        NeedsElevation = false;
        try
        {
            var dir = AppContext.BaseDirectory;
            var curFile = Environment.ProcessPath ?? Path.Combine(dir, "PcCareAgent.exe");
            var newFile = Path.Combine(dir, "PcCareAgent.new.exe");
            var backup = Path.Combine(dir, "PcCareAgent.old.exe");
            var bat = Path.Combine(dir, "update.bat");

            File.Copy(downloadedExe, newFile, overwrite: true);
            File.WriteAllText(bat, $"""
                @echo off
                cd /d "{dir}"
                ping -n 3 127.0.0.1 >nul
                net stop "{AgentConfig.ServiceName}"
                if exist "{backup}" del "{backup}"
                move /y "{curFile}" "{backup}" || (net start "{AgentConfig.ServiceName}" & exit /b 1)
                move /y "{newFile}" "{curFile}" || (move /y "{backup}" "{curFile}" & net start "{AgentConfig.ServiceName}" & exit /b 1)
                net start "{AgentConfig.ServiceName}"
                runas /trustlevel:0x20000 "cmd /c start "" "{curFile}""
                del "{bat}"
                """);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            NeedsElevation = true;
            return false;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Elevated helper mode (--apply-update): performs the swap for a standard user.</summary>
public static class ApplyUpdate
{
    public static void Run(string downloadedExe)
    {
        Thread.Sleep(2500); // give the non-elevated UI time to exit
        if (!Swap.TryStageAndLaunch(downloadedExe))
            MessageBox.Show("The update could not be installed. Please contact Matt's PC Repair.",
                "Matt's PC Repair — PC Care", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
