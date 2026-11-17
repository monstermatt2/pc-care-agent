using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Toolkit.Uwp.Notifications;

namespace PcCareAgent;

/// <summary>Runs in the logged-on user's session (launched by the service): shows the
/// "needs attention" toast; tapping it wakes the server-side AI brain to fix the issue.</summary>
public static class ToastHelper
{
    /// <summary>Extract the embedded M logo where the toast builder can reference it.</summary>
    private static string? LogoPath()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "pcare-logo128.png");
            if (!File.Exists(path))
            {
                using var s = typeof(ToastHelper).Assembly
                    .GetManifestResourceStream("PcCareAgent.resources.logo128.png");
                if (s is null) return null;
                using var fs = File.Create(path);
                s.CopyTo(fs);
            }
            return path;
        }
        catch { return null; }
    }

    public static void Run(string payload)
    {
        var parts = payload.Split("|||", 2);
        var issue = parts[0].Trim();
        var summary = parts.Length > 1 ? parts[1].Trim() : "";

        ToastNotificationManagerCompat.OnActivated += async _ =>
        {
            try
            {
                var cfg = AgentConfig.Load();
                using var http = new HttpClient { BaseAddress = new Uri(cfg.ServerUrl.TrimEnd('/') + "/") };
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", cfg.DeviceToken);

                var resp = await http.PostAsJsonAsync("api/v1/devices/me/fix", new { issue, summary });
                string followUp;
                if (resp.IsSuccessStatusCode)
                {
                    var fix = await resp.Content.ReadFromJsonAsync<FixResponse>();
                    followUp = fix?.Queued is { Count: > 0 }
                        ? $"AI repair started: {string.Join(", ", fix.Queued.Select(q => q.Type))}"
                        : "AI reviewed the PC — no automatic repair needed right now.";
                }
                else
                {
                    followUp = "Couldn't reach the repair service. Your technician still sees the alert.";
                }

                var b1 = new ToastContentBuilder()
                    .AddText("Matt's PC Repair");
                var logo1 = LogoPath();
                if (logo1 is not null) b1.AddAppLogoOverride(new Uri(logo1));
                b1.AddText(followUp).Show();
                await Task.Delay(5000);
            }
            catch { /* silent */ }
            Environment.Exit(0);
        };

        var b = new ToastContentBuilder()
            .AddText("Matt's PC Repair — attention needed");
        var logo = LogoPath();
        if (logo is not null) b.AddAppLogoOverride(new Uri(logo));
        b.AddText(string.IsNullOrWhiteSpace(summary) ? issue : summary)
            .AddText("Tap to let our AI repair it now.")
            .Show();

        // stay alive up to 15 min waiting for the tap
        var ctx = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
        timer.Tick += (_, _) => { timer.Stop(); Environment.Exit(0); };
        timer.Start();
        Application.Run(ctx);
    }

    private sealed record FixResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("summary")] string? Summary,
        [property: System.Text.Json.Serialization.JsonPropertyName("queued")] List<Queued>? Queued);

    private sealed record Queued(
        [property: System.Text.Json.Serialization.JsonPropertyName("type")] string? Type);
}
