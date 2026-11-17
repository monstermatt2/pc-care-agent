using System.Runtime.InteropServices;

namespace PcCareAgent;

/// <summary>Launches a process into the interactive console session (the service runs as
/// SYSTEM in session 0 — toasts and UI must live in the logged-on user's session).</summary>
public static class SessionLauncher
{
    // NOTE the homes: WTSGetActiveConsoleSessionId -> kernel32; WTSQueryUserToken -> wtsapi32
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token, string app, string cmdline, IntPtr sa1, IntPtr sa2,
        bool inherit, uint creation, IntPtr env, string curdir,
        ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    /// <summary>Show the "tap to fix" toast in the logged-on user's session. Fire-and-forget.</summary>
    public static void ShowToast(string issue, string summary)
    {
        try
        {
            var sid = WTSGetActiveConsoleSessionId();
            if (sid == 0xFFFFFFFF) return;                 // no console session
            if (!WTSQueryUserToken(sid, out var tok)) return;

            var exe = Environment.ProcessPath;
            if (exe is null || !File.Exists(exe)) return;

            // keep the command line quote-safe
            var payload = (issue + "|||" + summary)
                .Replace('"', '\'')
                .Replace("\r", " ").Replace("\n", " ");
            if (payload.Length > 480) payload = payload[..480];

            var cmdline = $"\"{exe}\" --toast \"{payload}\"";
            _ = CreateEnvironmentBlock(out var env, tok, false);

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default",
                dwFlags = 1,          // STARTF_USESHOWWINDOW
                wShowWindow = 0,      // SW_HIDE — toast only, no window
            };
            var ok = CreateProcessAsUser(tok, exe, cmdline, IntPtr.Zero, IntPtr.Zero,
                false, 0x400 /*CREATE_UNICODE_ENVIRONMENT — CreateEnvironmentBlock returns unicode*/, env, Path.GetDirectoryName(exe) ?? @"C:\Windows", ref si, out var pi);
            _ = ok; // best-effort: the diagnosis is on the server either way
        }
        catch
        {
            // never let a notification failure break the service cycle
        }
    }
}
