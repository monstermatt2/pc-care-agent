using System.Reflection;

namespace PcCareAgent;

/// <summary>Shared brand assets (embedded multi-size logo.ico).</summary>
public static class Brand
{
    /// <summary>The M logo as a window/taskbar icon, or null when unavailable.
    /// Direct Icon(icoStream) silently fails on PIL-written ICOs (PNG-compressed
    /// entries) and PNG streams throw outright — so the reliable path is the
    /// 128px PNG turned into an icon via GetHicon().</summary>
    public static Icon? LogoIcon()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PcCareAgent.resources.logo128.png");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                using var bmp = new Bitmap(ms);
                var h = bmp.GetHicon();
                var ic = Icon.FromHandle(h);
                GC.KeepAlive(ic);
                return ic;
            }
        }
        catch { /* fall through */ }

        try
        {
            using var s = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PcCareAgent.resources.logo.ico");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return new Icon(ms);
            }
        }
        catch { /* fall through */ }

        return null;
    }
}
