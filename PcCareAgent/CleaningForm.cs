using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PcCareAgent;

/// <summary>The cleaning window: round % ring, current target at the bottom, live from the agent.</summary>
public sealed class CleaningForm : Form
{
    private static readonly Color Accent = Color.FromArgb(46, 117, 182);
    private static readonly Color Good = Color.FromArgb(34, 154, 82);
    private static readonly Color Ink = Color.FromArgb(45, 52, 62);
    private static readonly Color Muted = Color.FromArgb(120, 128, 138);

    private readonly HttpClient _http;
    private readonly Guid _taskId;
    private readonly RingPanel _ring = new();
    private readonly Label _current = new();
    private readonly Label _freed = new();
    private readonly Label _done = new();
    private readonly Button _ok = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private bool _finished;

    public CleaningForm(HttpClient http, Guid taskId)
    {
        _http = http;
        _taskId = taskId;

        Text = "Cleaning — Matt's PC Repair";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 430);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.5F);

        _ring.Size = new Size(220, 220);
        _ring.Location = new Point((ClientSize.Width - 220) / 2, 24);
        Controls.Add(_ring);

        _current.Text = "Waiting for the cleaning engine…";
        _current.ForeColor = Ink;
        _current.Font = new Font("Segoe UI", 11F);
        _current.Location = new Point(16, 258);
        _current.Size = new Size(388, 24);
        _current.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(_current);

        _freed.Text = "0 MB freed so far";
        _freed.ForeColor = Muted;
        _freed.Location = new Point(16, 288);
        _freed.Size = new Size(388, 20);
        _freed.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(_freed);

        _done.Text = "";
        _done.ForeColor = Good;
        _done.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        _done.Location = new Point(16, 318);
        _done.Size = new Size(388, 26);
        _done.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(_done);

        _ok.Text = "OK";
        _ok.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        _ok.ForeColor = Color.White;
        _ok.BackColor = Accent;
        _ok.FlatStyle = FlatStyle.Flat;
        _ok.FlatAppearance.BorderSize = 0;
        _ok.Size = new Size(120, 38);
        _ok.Location = new Point((ClientSize.Width - 120) / 2, 360);
        _ok.Enabled = false;
        _ok.Click += (_, _) => Close();
        Controls.Add(_ok);

        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
        FormClosed += (_, _) => _timer.Stop();
    }

    private async Task PollAsync()
    {
        if (_finished) return;
        try
        {
            var t = await _http.GetFromJsonAsync<ActiveTask>("api/v1/devices/me/active-task");
            if (t is null || t.Id != _taskId) return;

            var p = t.Progress;
            if (t.Status is "pending" or "running" && p is not null)
            {
                _ring.Pct = Math.Clamp(p.Pct ?? 0, 0, 100);
                _current.Text = $"Cleaning: {p.Current ?? "…"}";
                if ((p.FreedMb ?? 0) > 0)
                    _freed.Text = $"{MainForm.FmtMb(p.FreedMb)} freed so far • {(p.Items ?? 0):N0} files";
                _ring.Invalidate();
            }
            else if (t.Status == "done")
            {
                var freed = t.Result?.FreedMb ?? p?.FreedMb ?? 0;
                var items = t.Result?.Items ?? p?.Items ?? 0;
                _ring.Pct = 100;
                _ring.Complete = true;
                _ring.Invalidate();
                _current.Text = "Cleaning complete";
                _done.Text = $"✓ {MainForm.FmtMb(freed)} freed • {items:N0} files";
                _freed.Text = "";
                _ok.Enabled = true;
                _finished = true;
                _timer.Stop();
            }
            else if (t.Status == "failed")
            {
                _current.Text = "Cleaning ran into a problem — your technician was notified.";
                _done.Text = "✗ Could not finish";
                _done.ForeColor = Color.FromArgb(200, 60, 50);
                _ok.Enabled = true;
                _finished = true;
                _timer.Stop();
            }
        }
        catch { /* transient — keep polling */ }
    }

    private sealed record ActiveTask(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("progress")] ProgressInfo? Progress,
        [property: JsonPropertyName("result")] ResultInfo? Result);

    private sealed record ProgressInfo(
        [property: JsonPropertyName("pct")] int? Pct,
        [property: JsonPropertyName("current")] string? Current,
        [property: JsonPropertyName("freedMb")] double? FreedMb,
        [property: JsonPropertyName("items")] long? Items);

    private sealed record ResultInfo(
        [property: JsonPropertyName("freedMb")] double? FreedMb,
        [property: JsonPropertyName("items")] long? Items);
}

/// <summary>Round progress ring with the percentage in the middle.</summary>
internal sealed class RingPanel : Panel
{
    private static readonly Color Accent = Color.FromArgb(46, 117, 182);
    private static readonly Color Good = Color.FromArgb(34, 154, 82);
    private static readonly Color Ink = Color.FromArgb(45, 52, 62);

    public int Pct { get; set; }
    public bool Complete { get; set; }

    public RingPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var rect = new Rectangle(16, 16, Width - 32, Height - 32);
        using var bg = new Pen(Color.FromArgb(228, 232, 237), 16);
        using var fg = new Pen(Complete ? Good : Accent, 16)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };
        g.DrawArc(bg, rect, -90, 360);
        if (Pct > 0)
            g.DrawArc(fg, rect, -90, (float)(360.0 * Pct / 100.0));

        TextRenderer.DrawText(g, $"{Pct}%",
            new Font("Segoe UI", 26F, FontStyle.Bold), ClientRectangle,
            Complete ? Good : Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
