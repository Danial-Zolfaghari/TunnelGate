using System.Diagnostics;
using System.Net.NetworkInformation;

namespace TunnelGate.Core;

public static class PortManager
{
    public static void CleanupStalePlink(Action<string> log)
    {
        var managed = Normalize(AppPaths.PlinkPath);
        foreach (var proc in Process.GetProcessesByName("plink"))
        {
            try
            {
                var path = Normalize(proc.MainModule?.FileName ?? "");
                if (path == managed)
                {
                    log($"[CLEANUP] Stopping stale managed Plink PID {proc.Id}.");
                    proc.Kill(true);
                    proc.WaitForExit(3000);
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }
    }

    public static bool IsTcpListening(int port)
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port); }
        catch { return false; }
    }

    public static string? ValidateProxyPort(int port)
    {
        if (port is < 1 or > 65535) return "Invalid local SOCKS port.";
        return IsTcpListening(port) ? $"Local port {port} is already in use." : null;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch { return path.Trim().ToLowerInvariant(); }
    }
}
