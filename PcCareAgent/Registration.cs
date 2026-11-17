using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PcCareAgent;

/// <summary>CLI: PcCareAgent.exe register --server https://api... --token enr_... [--name "Office PC"]</summary>
public static class Registration
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? server = GetArg(args, "--server");
        string? token = GetArg(args, "--token");
        string? name = GetArg(args, "--name");

        if (server is null || token is null)
        {
            Console.WriteLine("Usage: PcCareAgent.exe register --server <https://api...> --token <enroll-token> [--name \"Office PC\"]");
            return 2;
        }

        using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/") };
        var body = new
        {
            enrollToken = token,
            hostname = Environment.MachineName,
            name = name ?? Environment.MachineName,
            os = SystemInfo.OsDescription,
            agentVersion = Worker.AgentVersion,
        };
        using var resp = await http.PostAsJsonAsync("api/v1/devices/enroll", body);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Enrollment failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
            return 1;
        }
        var payload = await resp.Content.ReadFromJsonAsync<EnrollResponse>();
        if (payload?.DeviceToken is null || payload.DeviceId is null)
        {
            Console.Error.WriteLine("Enrollment response missing device credentials.");
            return 1;
        }

        var cfg = AgentConfig.Load();
        cfg.ServerUrl = server;
        cfg.DeviceId = payload.DeviceId;
        cfg.DeviceToken = payload.DeviceToken;
        cfg.Save();
        Console.WriteLine($"Registered device {payload.DeviceId}.");
        Console.WriteLine($"Config saved to {Path.Combine(AgentConfig.ConfigDir, "config.json")}");
        return 0;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private sealed record EnrollResponse(
        [property: JsonPropertyName("deviceId")] string? DeviceId,
        [property: JsonPropertyName("deviceToken")] string? DeviceToken);
}
