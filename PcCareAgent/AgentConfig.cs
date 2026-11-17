using System.Text.Json;

namespace PcCareAgent;

public sealed class AgentConfig
{
    public const string ServiceName = "PcCareAgent";

    public string ServerUrl { get; set; } = "https://api.monstermatt2.net";
    public string? DeviceId { get; set; }
    public string? DeviceToken { get; set; }
    public int CheckIntervalSec { get; set; } = 60;
    public int UpdateIntervalSec { get; set; } = 6 * 3600;

    public static string ConfigDir =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcCareAgent")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pc-care");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath)) ?? new AgentConfig();
        }
        catch { /* fall through to defaults */ }
        return new AgentConfig();
    }
}
