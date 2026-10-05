namespace TunnelGate.Core;

public static class AppPaths
{
    public static string BaseDir => AppContext.BaseDirectory;
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TunnelGate");
    public static string VaultFile => Path.Combine(DataDir, "vault.tgv");
    public static string LockoutFile => Path.Combine(DataDir, "lockout.json");
    public static string SessionFile => Path.Combine(DataDir, "session.key");
    public static string LogFile => Path.Combine(DataDir, "TunnelGate.log");
    public static string ToolsDir => Path.Combine(DataDir, "tools");
    public static string PlinkPath => Path.Combine(ToolsDir, "plink.exe");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ToolsDir);
    }
}
