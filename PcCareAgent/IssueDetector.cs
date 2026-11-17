using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcCareAgent;

/// <summary>Local issue heuristics. The agent prompts the AI brain by itself when these fire
/// (or on first run) — clients never write prompts.</summary>
public static class IssueDetector
{
    public static List<string> Detect(DeviceSnapshot snapshot, DefenderFacts? defender, ServiceFacts? services = null)
    {
        var issues = new List<string>();

        foreach (var d in snapshot.Disks)
        {
            if (d.FreeGb < 10)
                issues.Add($"disk {d.Name} nearly full ({d.FreeGb} GB free of {d.TotalGb} GB)");
            else if (d.TotalGb > 20 && d.FreeGb / d.TotalGb < 0.05)
                issues.Add($"disk {d.Name} over 95% full ({d.FreeGb} GB free of {d.TotalGb} GB)");
        }

        if (snapshot.RamTotalMb is > 0 and < 4000)
            issues.Add($"low total RAM ({snapshot.RamTotalMb} MB)");

        if (snapshot.UptimeHours > 336)
            issues.Add($"uptime {snapshot.UptimeHours}h — overdue for a reboot");

        if (defender is not null)
        {
            if (defender.RealTimeProtectionEnabled == false) issues.Add("Defender real-time protection is OFF");
            if (defender.AMServiceEnabled == false) issues.Add("Defender antimalware service is disabled");
            if (defender.SignatureAgeDays is > 7)
                issues.Add($"Defender signatures are {defender.SignatureAgeDays:0.#} days old");
        }

        if (services is not null && services.WuEnabled == false)
            issues.Add("Windows Update service is disabled — the PC cannot receive security updates");

        return issues;
    }
}

/// <summary>Parsed subset of Get-Service for critical services.</summary>
public sealed record ServiceFacts(
    bool? WuEnabled,
    bool? WuRunning)
{
    public static ServiceFacts? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? S(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var state = S("State");
            var startMode = S("StartMode");
            return new ServiceFacts(
                startMode is null ? null : startMode != "Disabled",
                state is null ? null : state == "Running");
        }
        catch { return null; }
    }
}

/// <summary>Parsed subset of Get-MpComputerStatus.</summary>
public sealed record DefenderFacts(
    bool? RealTimeProtectionEnabled,
    bool? AMServiceEnabled,
    DateTime? SignatureLastUpdated)
{
    public double? SignatureAgeDays =>
        SignatureLastUpdated is { } t ? (DateTime.UtcNow - t.ToUniversalTime()).TotalDays : null;

    public static DefenderFacts? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool? B(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : (bool?)null;
            DateTime? D(string name)
            {
                if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
                return DateTime.TryParse(v.GetString(), out var dt) ? dt : null;
            }
            return new DefenderFacts(B("RealTimeProtectionEnabled"), B("AMServiceEnabled"), D("AntivirusSignatureLastUpdated"));
        }
        catch { return null; }
    }
}

public sealed record TaskOutcome(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("result")] string Result);
