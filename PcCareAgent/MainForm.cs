using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace PcCareAgent;

/// <summary>"Matt's PC Repair — PC Care" client dashboard (CCleaner-style two-panel layout).</summary>
public sealed class MainForm : Form
{
    private static readonly Color Navy = Color.FromArgb(30, 41, 59);
    private static readonly Color Accent = Color.FromArgb(46, 117, 182);
    private static readonly Color Ink = Color.FromArgb(45, 52, 62);
    private static readonly Color Muted = Color.FromArgb(120, 128, 138);
    private static readonly Color Good = Color.FromArgb(34, 154, 82);

    /// <summary>(target id, label, default checked) — mirrors the agent's deep_clean catalog.</summary>
    private static readonly (string Id, string Label, bool On)[] CleanOptions =
    {
        ("user_temp", "User temp files", true),
        ("windows_temp", "Windows temp files", true),
        ("chrome_cache", "Chrome cache", true),
        ("edge_cache", "Edge cache", true),
        ("firefox_cache", "Firefox cache", true),
        ("update_cache", "Windows Update cache", true),
        ("error_reports", "Error reports", true),
        ("dumps", "Memory dumps", true),
        ("delivery_opt", "Delivery Optimization", false),
        ("thumbnails", "Thumbnail cache", true),
        ("shader_cache", "Shader cache", true),
        ("crash_dumps", "Crash dumps", true),
        ("recycle_bin", "Recycle Bin", true),
        ("dns_flush", "DNS cache (privacy)", true),
    };

    private readonly AgentConfig _cfg = AgentConfig.Load();
    private readonly HttpClient _http = new();
    private readonly List<CheckBox> _cleanChecks = new();
    private CheckBox _selectAll = null!;

    private readonly Label _protectLine = new();
    private readonly Label _cleanStat = new();
    private readonly Label _sysOs = new();
    private readonly Label _sysRam = new();
    private readonly Label _sysDisk = new();
    private readonly Label _footLine = new();

    private readonly Label _cleanStatus = new();
    private readonly Label _cleanResult = new();
    private readonly ProgressBar _diskBar = new();
    private readonly Label _diskLabel = new();
    private readonly Label _monitorLine = new();
    private readonly Label _remoteLine = new();
    private readonly Button _helpBtn = new();

    public MainForm()
    {
        Text = "Matt's PC Repair — PC Care";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 560);
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

        BuildLeftPanel();
        BuildRightPanel();

        _http.BaseAddress = new Uri(_cfg.ServerUrl.TrimEnd('/') + "/");
        if (_cfg.DeviceToken is not null)
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _cfg.DeviceToken);

        // flashing "New update" button (top-right) when the server has a newer build
        _updateBtn = new Button
        {
            Text = "\u25cf  New update",
            Visible = false,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(245, 158, 11),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(ClientSize.Width - 130, 10),
        };
        _updateBtn.FlatAppearance.BorderSize = 0;
        _updateBtn.Click += (_, _) => { RestartForUpdate = true; Close(); };
        Controls.Add(_updateBtn);
        _updateBtn.BringToFront();
        _flashTimer = new System.Windows.Forms.Timer { Interval = 550 };
        var flashOn = false;
        _flashTimer.Tick += (_, _) =>
        {
            flashOn = !flashOn;
            _updateBtn.BackColor = flashOn ? Color.FromArgb(239, 68, 68) : Color.FromArgb(245, 158, 11);
        };

        Load += async (_, _) =>
        {
            await RefreshAsync();
            _ = Task.Run(async () =>
            {
                while (!IsDisposed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    try { await RefreshAsync(); } catch { /* transient */ }
                }
            });
        };

        _ = WatchForUpdateAsync();
    }

    /// <summary>Set by the update button: the launcher loop reruns the update check.</summary>
    public static bool RestartForUpdate;

    private Button? _updateBtn;
    private System.Windows.Forms.Timer? _flashTimer;

    /// <summary>Every ~5 min: if the server has a newer build, show the flashing button.</summary>
    private async Task WatchForUpdateAsync()
    {
        var api = new ApiClient(_http, _cfg);
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);
        while (!IsDisposed)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var info = await api.CheckUpdateAsync(current, cts.Token);
                if (info is not null && !IsDisposed && _updateBtn is not null && !_updateBtn.Visible)
                    BeginInvoke(() => { _updateBtn.Visible = true; _flashTimer?.Start(); });
            }
            catch { /* offline: silent */ }
            await Task.Delay(TimeSpan.FromMinutes(5));
        }
    }

    // ---------------- left panel: brand + health ----------------
    private void BuildLeftPanel()
    {
        var left = new Panel { Dock = DockStyle.Left, Width = 270, BackColor = Navy };
        Controls.Add(left);

        var logo = new PictureBox
        {
            Location = new Point(24, 26),
            Size = new Size(56, 56),
            SizeMode = PictureBoxSizeMode.StretchImage,
        };
        try
        {
            using var s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PcCareAgent.resources.logo128.png");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                logo.Image = Image.FromStream(new MemoryStream(ms.ToArray()));
            }
        }
        catch { /* logo optional */ }
        left.Controls.Add(logo);

        left.Controls.Add(new Label
        {
            Text = "Matt's PC Repair",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            Location = new Point(24, 88),
            AutoSize = true,
        });
        left.Controls.Add(new Label
        {
            Text = "P C   C A R E",
            ForeColor = Color.FromArgb(147, 197, 253),
            Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
            Location = new Point(25, 116),
            AutoSize = true,
        });

        _protectLine.Text = "Checking status…";
        _protectLine.ForeColor = Color.FromArgb(226, 232, 240);
        _protectLine.Location = new Point(24, 168);
        _protectLine.AutoSize = true;
        left.Controls.Add(_protectLine);

        _cleanStat.Text = "Last deep clean: —";
        _cleanStat.ForeColor = Color.FromArgb(148, 163, 184);
        _cleanStat.Location = new Point(24, 192);
        _cleanStat.AutoSize = true;
        left.Controls.Add(_cleanStat);

        left.Controls.Add(new Panel
        {
            BackColor = Color.FromArgb(51, 65, 85),
            Location = new Point(24, 232),
            Size = new Size(222, 1),
        });

        left.Controls.Add(new Label
        {
            Text = "SYSTEM",
            ForeColor = Color.FromArgb(100, 116, 139),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Location = new Point(24, 246),
            AutoSize = true,
        });
        _sysOs.Text = "—";
        _sysOs.ForeColor = Color.FromArgb(203, 213, 225);
        _sysOs.Location = new Point(24, 270);
        _sysOs.AutoSize = true;
        _sysRam.Text = "";
        _sysRam.ForeColor = Color.FromArgb(203, 213, 225);
        _sysRam.Location = new Point(24, 292);
        _sysRam.AutoSize = true;
        _sysDisk.Text = "";
        _sysDisk.ForeColor = Color.FromArgb(203, 213, 225);
        _sysDisk.Location = new Point(24, 314);
        _sysDisk.AutoSize = true;
        left.Controls.AddRange(new Control[] { _sysOs, _sysRam, _sysDisk });

        _footLine.Text = "";
        _footLine.ForeColor = Color.FromArgb(100, 116, 139);
        _footLine.Location = new Point(24, 508);
        _footLine.AutoSize = true;
        left.Controls.Add(_footLine);
    }

    // ---------------- right panel: clean + storage + protection ----------------
    private void BuildRightPanel()
    {
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        Controls.Add(right);
        right.BringToFront();

        right.Controls.Add(new Label
        {
            Text = "Quick Clean",
            ForeColor = Ink,
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            Location = new Point(28, 20),
            AutoSize = true,
        });
        right.Controls.Add(new Label
        {
            Text = "Choose what to clean:",
            ForeColor = Muted,
            Location = new Point(28, 56),
            AutoSize = true,
        });
        _selectAll = new CheckBox
        {
            Text = "Select all",
            ForeColor = Muted,
            Location = new Point(470, 54),
            AutoSize = true,
            Checked = true,
        };
        _selectAll.CheckedChanged += (_, _) =>
        {
            foreach (var c in _cleanChecks) c.Checked = _selectAll.Checked;
        };
        right.Controls.Add(_selectAll);

        // checklist: 2 columns x 7 rows
        for (var i = 0; i < CleanOptions.Length; i++)
        {
            var (id, label, on) = CleanOptions[i];
            var cb = new CheckBox
            {
                Tag = id,
                Text = label,
                ForeColor = Ink,
                Font = new Font("Segoe UI", 9F),
                Checked = on,
                AutoSize = true,
                Location = new Point(28 + (i % 2) * 240, 82 + i / 2 * 22),
            };
            _cleanChecks.Add(cb);
            right.Controls.Add(cb);
        }

        var runBtn = new Button
        {
            Text = "Run Quick Clean",
            Font = new Font("Segoe UI", 11.5F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Accent,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(210, 44),
            Location = new Point(28, 248),
            Cursor = Cursors.Hand,
        };
        runBtn.FlatAppearance.BorderSize = 0;
        runBtn.Click += async (_, _) => await RunCleanAsync();
        right.Controls.Add(runBtn);

        _cleanStatus.Text = "";
        _cleanStatus.ForeColor = Muted;
        _cleanStatus.Location = new Point(28, 302);
        _cleanStatus.AutoSize = true;
        right.Controls.Add(_cleanStatus);

        _cleanResult.Text = "";
        _cleanResult.ForeColor = Good;
        _cleanResult.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        _cleanResult.Location = new Point(250, 304);
        _cleanResult.AutoSize = true;
        right.Controls.Add(_cleanResult);

        // storage
        right.Controls.Add(new Label
        {
            Text = "Storage",
            ForeColor = Ink,
            Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
            Location = new Point(28, 330),
            AutoSize = true,
        });
        _diskBar.Location = new Point(28, 358);
        _diskBar.Size = new Size(520, 10);
        right.Controls.Add(_diskBar);
        _diskLabel.Text = "—";
        _diskLabel.ForeColor = Muted;
        _diskLabel.Location = new Point(28, 372);
        _diskLabel.AutoSize = true;
        right.Controls.Add(_diskLabel);

        // protection
        right.Controls.Add(new Label
        {
            Text = "Protection",
            ForeColor = Ink,
            Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
            Location = new Point(28, 400),
            AutoSize = true,
        });
        _monitorLine.Text = "●  Monitoring active";
        _monitorLine.ForeColor = Good;
        _monitorLine.Font = new Font("Segoe UI", 10F);
        _monitorLine.Location = new Point(28, 428);
        _monitorLine.AutoSize = true;
        right.Controls.Add(_monitorLine);

        _remoteLine.Text = "Remote help: —";
        _remoteLine.ForeColor = Muted;
        _remoteLine.Font = new Font("Segoe UI", 10F);
        _remoteLine.Location = new Point(28, 450);
        _remoteLine.AutoSize = true;
        right.Controls.Add(_remoteLine);

        _helpBtn.Text = "Get Remote Help";
        _helpBtn.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _helpBtn.ForeColor = Accent;
        _helpBtn.BackColor = Color.White;
        _helpBtn.FlatStyle = FlatStyle.Flat;
        _helpBtn.FlatAppearance.BorderColor = Accent;
        _helpBtn.Size = new Size(180, 36);
        _helpBtn.Location = new Point(28, 474);
        _helpBtn.Cursor = Cursors.Hand;
        _helpBtn.Click += (_, _) =>
        {
            var rd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
            if (!File.Exists(rd))
            {
                MessageBox.Show("Remote help isn't installed on this PC yet.\nYour technician can enable it remotely.",
                    "Matt's PC Repair", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo(rd) { UseShellExecute = true });
        };
        right.Controls.Add(_helpBtn);

        // bottom feature strip
        var tiles = new[]
        {
            ("●  Privacy protected", Good),
            ("●  Performance tuned", Good),
            ("●  Deep clean ready", Good),
            ("●  Health monitored", Good),
        };
        for (var i = 0; i < tiles.Length; i++)
        {
            var l = new Label
            {
                Text = tiles[i].Item1,
                ForeColor = tiles[i].Item2,
                BackColor = Color.FromArgb(243, 246, 249),
                Font = new Font("Segoe UI", 8.5F),
                Size = new Size(186, 30),
                Location = new Point(28 + i * 196, 522),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            right.Controls.Add(l);
        }
    }

    // ---------------- actions ----------------
    private async Task RunCleanAsync()
    {
        var targets = _cleanChecks.Where(c => c.Checked).Select(c => (string)c.Tag!).ToList();
        if (targets.Count == 0)
        {
            _cleanStatus.Text = "Select at least one item to clean.";
            return;
        }
        _cleanStatus.Text = "Starting…";
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/devices/me/tasks",
                new { type = "deep_clean", targets });
            if (!resp.IsSuccessStatusCode)
            {
                _cleanStatus.Text = "Couldn't reach the PC Care server — will keep retrying.";
                return;
            }
            var queued = await resp.Content.ReadFromJsonAsync<QueuedTask>();
            _cleanStatus.Text = "Cleaning…";
            using var dlg = new CleaningForm(_http, queued!.Id);
            dlg.ShowDialog(this);
            _cleanStatus.Text = "Done";
            await RefreshAsync();
        }
        catch
        {
            _cleanStatus.Text = "Couldn't reach the PC Care server — will keep retrying.";
        }
    }

    // ---------------- data ----------------
    private async Task RefreshAsync()
    {
        if (_cfg.DeviceToken is null)
        {
            _protectLine.Text = "Not set up yet — your technician will activate PC Care.";
            return;
        }
        DeviceInfo? info = null;
        try { info = await _http.GetFromJsonAsync<DeviceInfo>("api/v1/devices/me/info"); }
        catch { /* transient */ }
        if (info is null || IsDisposed) return;

        _protectLine.Text = info.IsHealthy != false
            ? "●  Your PC is protected"
            : "●  Needs attention — technician notified";
        _protectLine.ForeColor = info.IsHealthy != false
            ? Color.FromArgb(134, 239, 172)
            : Color.FromArgb(253, 224, 71);

        if (info.LastClean is not null)
            _cleanStat.Text = $"Last deep clean: {FmtMb(info.LastClean.FreedMb)} freed";

        _sysOs.Text = info.Os ?? "—";
        _sysRam.Text = info.RamMb is > 0 ? $"{info.RamMb / 1024.0:0.#} GB memory • {info.CpuCount ?? 0} CPU threads" : "";
        _sysDisk.Text = info.DiskSummary ?? "";

        _footLine.Text = $"agent {info.AgentVersion} • checked in {ToLocal(info.LastSeen)}";

        _diskBar.Value = Math.Clamp((int)(info.DiskUsedPct ?? 0), 0, 100);
        _diskLabel.Text = info.DiskSummary ?? "—";

        if (info.LastClean is not null)
            _cleanResult.Text = $"✓ last clean: {FmtMb(info.LastClean.FreedMb)} • {info.LastClean.Items:N0} files";

        _monitorLine.Text = info.IsHealthy != false
            ? "●  Monitoring active"
            : "●  Monitoring active — issues found";
        _remoteLine.Text = string.IsNullOrEmpty(info.RustdeskId)
            ? "Remote help: not set up yet"
            : $"Remote help ready — client number {info.RustdeskId}";
    }

    internal static string FmtMb(double? mb) => mb switch
    {
        >= 1024 => $"{mb / 1024.0:0.0} GB",
        > 0 => $"{mb:0} MB",
        _ => "0 MB",
    };

    private static string ToLocal(string? iso) =>
        DateTime.TryParse(iso, out var t) ? t.ToLocalTime().ToString("ddd HH:mm") : "recently";

    private sealed record QueuedTask([property: JsonPropertyName("id")] Guid Id);

    private sealed record DeviceInfo(
        [property: JsonPropertyName("agentVersion")] string? AgentVersion,
        [property: JsonPropertyName("lastSeen")] string? LastSeen,
        [property: JsonPropertyName("rustdeskId")] string? RustdeskId,
        [property: JsonPropertyName("isHealthy")] bool? IsHealthy,
        [property: JsonPropertyName("os")] string? Os,
        [property: JsonPropertyName("ramMb")] int? RamMb,
        [property: JsonPropertyName("cpuCount")] int? CpuCount,
        [property: JsonPropertyName("diskSummary")] string? DiskSummary,
        [property: JsonPropertyName("diskUsedPct")] double? DiskUsedPct,
        [property: JsonPropertyName("lastClean")] CleanInfo? LastClean);

    private sealed record CleanInfo(
        [property: JsonPropertyName("freedMb")] double? FreedMb,
        [property: JsonPropertyName("items")] long? Items);
}
