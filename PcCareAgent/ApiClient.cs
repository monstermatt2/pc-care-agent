using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcCareAgent;

public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly AgentConfig _config;

    public ApiClient(HttpClient http, AgentConfig config)
    {
        _config = config;
        _http = http;
        _http.BaseAddress = new Uri(_config.ServerUrl.TrimEnd('/') + "/");
        if (_config.DeviceToken is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.DeviceToken);
    }

    public async Task<bool> CheckInAsync(object snapshot, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("api/v1/devices/me/checkins", snapshot, ct);
        resp.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<AgentTask?> GetNextTaskAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("api/v1/devices/me/tasks", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AgentTask>(cancellationToken: ct);
    }

    public async Task ReportTaskAsync(Guid id, string status, object result, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"api/v1/devices/me/tasks/{id}/result",
            new { status, result }, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Live progress for the client's cleaning window (pct + current target + freed so far).</summary>
    public async Task ReportProgressAsync(Guid taskId, int pct, string current, double freedMb, long items, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("api/v1/devices/me/progress",
            new { taskId, pct, current, freedMb = Math.Round(freedMb, 1), items }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<UpdateInfo?> CheckUpdateAsync(Version current, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"api/v1/devices/me/update?version={current}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<UpdateInfo>(cancellationToken: ct);
    }

    public async Task<byte[]> DownloadAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Ask the server-side AI brain for a diagnosis (agent-initiated, no client prompting).</summary>
    public async Task<Diagnosis?> DiagnoseAsync(
        object snapshot, List<string> issues, List<string> survey, List<TaskOutcome> recentResults, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("api/v1/devices/me/diagnose",
            new { snapshot, issues, survey, recentResults }, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Diagnosis>(cancellationToken: ct);
    }

    /// <summary>Report the RustDesk client number + rotating password so the tech can connect.</summary>
    public async Task ReportRemoteAsync(string rustdeskId, string password, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("api/v1/devices/me/remote",
            new { rustdeskId, password }, ct);
        resp.EnsureSuccessStatusCode();
    }
}

public sealed record Diagnosis(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("recommendations")] List<Recommendation>? Recommendations);

public sealed record Recommendation(
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record DefenderScanOutcome(
    [property: JsonPropertyName("scanType")] string ScanType,
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("defenderStatusJson")] string DefenderStatusJson);

public sealed record UpdateInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed record AgentTask(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("params")] Dictionary<string, JsonElement>? Params);
