namespace PcCareAgent;

/// <summary>Cross-process "last toast" gate — a service crash/restart loop must not
/// machine-gun toasts into the user's session. Persisted in the config dir.</summary>
public static class ToastGate
{
    private static string Path => System.IO.Path.Combine(AgentConfig.ConfigDir, "toast-gate.txt");

    /// <summary>True when the last toast is older than the window (default 30 min).</summary>
    public static bool Allow(TimeSpan? window = null)
    {
        try
        {
            var w = window ?? TimeSpan.FromMinutes(30);
            var t = System.IO.File.GetLastWriteTimeUtc(Path);
            return DateTime.UtcNow - t > w;
        }
        catch { return true; } // no file yet -> allowed
    }

    public static void Mark()
    {
        try { System.IO.File.WriteAllText(Path, DateTime.UtcNow.ToString("o")); }
        catch { /* best effort */ }
    }
}
