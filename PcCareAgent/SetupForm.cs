using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace PcCareAgent;

/// <summary>First-run setup on a fresh machine — modern Win11-style installer:
/// borderless rounded window, navy hero panel with the M logo, one accent
/// button, animated progress. One click registers the PC (server-minted
/// one-time key + hardware fingerprint), installs + starts the background
/// service (one UAC prompt), and launches the app from Program Files.</summary>
public sealed class SetupForm : Form
{
    public static bool Completed { get; private set; }

    private const string Server = "https://api.monstermatt2.net";

    private static readonly Color Navy = Color.FromArgb(13, 27, 62);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color Ink = Color.FromArgb(26, 30, 38);

    private readonly ModernButton _install = new("Install on this PC", Accent);
    private readonly Label _status = new();
    private readonly ModernProgress _progress = new();
    private readonly Panel _hero = new();
    private readonly System.Windows.Forms.Timer _closeTimer = new();
    private bool _dragging;
    private Point _dragStart;

    public SetupForm()
    {
        Text = "Matt's PC Repair — PC Care";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 360);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9.5F);

        try
        {
            using var s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PcCareAgent.resources.logo.ico");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                Icon = new Icon(ms);
            }
        }
        catch { /* logo optional */ }

        RoundedCorners(12);

        // ---- left hero panel (brand) — everything painted directly: child
        // Labels have opaque default backgrounds and render as white boxes
        // over the gradient (white-on-white = invisible text) ----
        var heroLogo = LoadLogo();
        _hero.Dock = DockStyle.Left;
        _hero.Width = 225;
        _hero.Paint += (s, e) =>
        {
            var g = e.Graphics;
            using (var lg = new LinearGradientBrush(_hero.ClientRectangle, Navy, Color.FromArgb(23, 46, 102), 65F))
                g.FillRectangle(lg, _hero.ClientRectangle);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (heroLogo is not null)
                g.DrawImage(heroLogo, new Rectangle(64, 76, 96, 96));
            TextRenderer.DrawText(g, "MATT'S",
                new Font("Segoe UI", 17F, FontStyle.Bold), new Point(26, 208), Color.White);
            TextRenderer.DrawText(g, "PC CARE",
                new Font("Segoe UI", 17F, FontStyle.Bold), new Point(26, 238), Color.White);
            TextRenderer.DrawText(g, "Protected by Matt's PC Repair",
                new Font("Segoe UI", 8.8F), new Point(28, 292), Color.FromArgb(163, 186, 240));
        };
        Controls.Add(_hero);

        // ---- right content ----
        const int x = 260;
        Controls.Add(new Label
        {
            Text = "Welcome",
            Font = new Font("Segoe UI", 20F, FontStyle.Bold),
            ForeColor = Ink,
            Location = new Point(x, 64),
            AutoSize = true,
        });
        Controls.Add(new Label
        {
            Text = "One click installs background protection and opens\r\n" +
                   "the PC Care app. This PC is registered automatically.",
            Font = new Font("Segoe UI", 9.8F),
            ForeColor = Color.FromArgb(71, 78, 90),
            Location = new Point(x, 108),
            Size = new Size(330, 44),
        });

        _progress.Location = new Point(x, 190);
        _progress.Size = new Size(330, 5);
        _progress.Visible = false;
        Controls.Add(_progress);

        _status.Font = new Font("Segoe UI", 9.2F);
        _status.ForeColor = Color.FromArgb(71, 78, 90);
        _status.Location = new Point(x, 210);
        _status.Size = new Size(340, 46);
        Controls.Add(_status);

        _install.Location = new Point(x, 286);
        _install.Size = new Size(190, 42);
        _install.Click += async (_, _) => await InstallAsync();
        Controls.Add(_install);

        // ---- chrome: drag anywhere on the content + custom close ----
        var close = new ModernCloseButton { Location = new Point(ClientSize.Width - 46, 8) };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        close.BringToFront();
        MouseDown += DragStart;
        MouseMove += DragMove;
        MouseUp += (_, _) => _dragging = false;
        _hero.MouseDown += DragStart;
        _hero.MouseMove += DragMove;
        _hero.MouseUp += (_, _) => _dragging = false;
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Escape or Keys.Enter && !_install.Busy) Close(); };

        _closeTimer.Interval = 1400;
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); BeginInvoke(Close); };
    }

    private void DragStart(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; }
    }
    private void DragMove(object? s, MouseEventArgs e)
    {
        if (_dragging) Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
    }

    private static Image? LoadLogo()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PcCareAgent.resources.logo128.png");
            if (s is null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Image.FromStream(new MemoryStream(ms.ToArray()));
        }
        catch { return null; }
    }

    private void RoundedCorners(int r)
    {
        using var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(0, 0, r * 2, r * 2, 180, 90);
        p.AddArc(Width - r * 2, 0, r * 2, r * 2, 270, 90);
        p.AddArc(Width - r * 2, Height - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(0, Height - r * 2, r * 2, r * 2, 90, 90);
        Region = new Region(p);
    }

    private void Set(string s, bool working = true) => BeginInvoke(() =>
    {
        _status.Text = s;
        _status.ForeColor = Color.FromArgb(71, 78, 90);
        _progress.Visible = working;
    });

    private void Err(string s) => BeginInvoke(() =>
    {
        _status.Text = s;
        _status.ForeColor = Color.FromArgb(196, 43, 28);
        _progress.Visible = false;
        _install.Busy = false;
    });

    private async Task InstallAsync()
    {
        _install.Busy = true;
        try
        {
            // step 1: ask the server for a one-time install key (random per install)
            Set("Contacting Matt's PC Care…");
            using var http = new HttpClient { BaseAddress = new Uri(Server.TrimEnd('/') + "/") };
            using var keyResp = await http.PostAsJsonAsync("api/v1/devices/install-key",
                new { hostname = Environment.MachineName });
            if (!keyResp.IsSuccessStatusCode)
            {
                var kb = (await keyResp.Content.ReadAsStringAsync()).Trim();
                Err("Could not get an install key — " + kb[..Math.Min(80, kb.Length)]);
                return;
            }
            var key = (await keyResp.Content.ReadFromJsonAsync<InstallKeyResponse>())?.Key;
            if (string.IsNullOrEmpty(key))
            {
                Err("Install key response was incomplete — click Install again.");
                return;
            }

            // step 2: enroll with that key (hardware fingerprint = reinstall identity)
            Set("Registering this PC…");
            var fp = Fingerprint.Compute();
            using var resp = await http.PostAsJsonAsync("api/v1/devices/enroll", new
            {
                enrollToken = key,
                hostname = Environment.MachineName,
                name = Environment.MachineName,
                fingerprint = fp,
                os = SystemInfo.OsDescription,
                agentVersion = Worker.AgentVersion,
            });
            if (!resp.IsSuccessStatusCode)
            {
                var body = (await resp.Content.ReadAsStringAsync()).Trim();
                Err("Registration failed: " + body[..Math.Min(120, body.Length)]);
                return;
            }
            var payload = await resp.Content.ReadFromJsonAsync<EnrollResponse>();
            if (payload?.DeviceToken is null || payload.DeviceId is null)
            {
                Err("Registration response was incomplete — try again.");
                return;
            }
            var cfg = AgentConfig.Load();
            cfg.ServerUrl = Server;
            cfg.DeviceId = payload.DeviceId;
            cfg.DeviceToken = payload.DeviceToken;
            cfg.Save();

            Set("Installing the background service…\r\n(approve the Windows prompt)");
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--install-service")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                });
                p?.WaitForExit();
            }
            catch
            {
                Err("The install needs the Windows admin prompt — click Install again and approve it.");
                return;
            }

            // success state, then the elevated installer's app launch takes over
            BeginInvoke(() =>
            {
                _progress.Visible = false;
                _status.Text = "All set — starting Matt's PC Care…";
                _status.ForeColor = Color.FromArgb(21, 128, 61);
                _install.Text = "✓ Installed";
            });
            Completed = true;
            _closeTimer.Start();
        }
        catch (Exception ex)
        {
            Err(ex.Message);
        }
    }

    private sealed record InstallKeyResponse([property: JsonPropertyName("key")] string? Key);

    private sealed record EnrollResponse(
        [property: JsonPropertyName("deviceId")] string? DeviceId,
        [property: JsonPropertyName("deviceToken")] string? DeviceToken);
}

/// <summary>Rounded accent button with hover/press states and a Busy lock.</summary>
public sealed class ModernButton : Control
{
    private readonly Color _base;
    private bool _hover, _press;

    public bool Busy { get; set; }

    public ModernButton(string text, Color color)
    {
        _base = color;
        Text = text;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 10.3F, FontStyle.Bold);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _press = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var c = Busy ? ControlPaint.Dark(_base, 0.15f)
              : _press ? ControlPaint.Light(_base, 0.15f)
              : _hover ? ControlPaint.Light(_base, 0.07f)
              : _base;
        using var path = new GraphicsPath();
        var r = 8;
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(Width - r * 2, 0, r * 2, r * 2, 270, 90);
        path.AddArc(Width - r * 2, Height - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(0, Height - r * 2, r * 2, r * 2, 90, 90);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(c)) e.Graphics.FillPath(b, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
            Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Indeterminate rounded progress bar (sliding accent pill).</summary>
public sealed class ModernProgress : Control
{
    private float _t;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 24 };

    public ModernProgress()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _timer.Tick += (_, _) => { _t += 0.024f; Invalidate(); };
        VisibleChanged += (_, _) => { if (Visible) _timer.Start(); else _timer.Stop(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var track = new SolidBrush(Color.FromArgb(228, 232, 240)))
            e.Graphics.FillRounded(track, ClientRectangle, Height / 2);
        // pill sweeps left -> right -> left
        var span = Width - 70;
        var pos = (MathF.Sin(_t * MathF.PI) + 1f) / 2f * span;
        var pill = new RectangleF(pos, 0, 70, Height);
        using (var b = new SolidBrush(Color.FromArgb(37, 99, 235)))
            e.Graphics.FillRounded(b, pill, Height / 2);
    }
}

/// <summary>Borderless-window close glyph with red hover.</summary>
public sealed class ModernCloseButton : Control
{
    private bool _hover;

    public ModernCloseButton()
    {
        Size = new Size(38, 34);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_hover) using (var b = new SolidBrush(Color.FromArgb(232, 17, 35))) e.Graphics.FillRectangle(b, ClientRectangle);
        using var p = new Pen(_hover ? Color.White : Color.FromArgb(90, 98, 112), 1.6f);
        var cx = Width / 2; var cy = Height / 2; var s = 5;
        e.Graphics.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
        e.Graphics.DrawLine(p, cx + s, cy - s, cx - s, cy + s);
    }
}

/// <summary>Elevated one-shot (--install-service): copy to Program Files, create +
/// start the service, then launch the app UI non-elevated from the installed path.</summary>
public static class Installer
{
    public static void Run()
    {
        try
        {
            var src = Environment.ProcessPath;
            if (src is null) return;
            var dir = @"C:\Program Files\PcCare";
            Directory.CreateDirectory(dir);
            var dst = Path.Combine(dir, "PcCareAgent.exe");
            if (!string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                File.Copy(src, dst, overwrite: true);

            var svc = AgentConfig.ServiceName;
            TaskRunner.RunExe("sc.exe", $"create \"{svc}\" binPath= \"{dst}\" start= auto", 30);
            TaskRunner.RunExe("sc.exe", $"config \"{svc}\" binPath= \"{dst}\" start= auto", 30); // idempotent reinstall
            TaskRunner.RunExe("sc.exe", $"start \"{svc}\"", 30);

            // open the app (unelevated) from the installed location — the launcher
            // check screen runs there, not from the Downloads copy
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c runas /trustlevel:0x20000 \"{dst}\"")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            });
        }
        catch
        {
            MessageBox.Show("Installation failed. Please contact Matt's PC Repair.",
                "Matt's PC Repair — PC Care", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

file static class GraphicsExtensions
{
    public static void FillRounded(this Graphics g, Brush b, RectangleF r, int radius)
    {
        using var p = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        g.FillPath(b, p);
    }
}
