using System.Windows;
using TunnelGate.Core;
using TunnelGate.UI;

namespace TunnelGate;

public partial class App : Application
{
    public static AppRuntime Runtime { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.Ensure();
        Runtime = new AppRuntime();

        // A DPAPI-protected session key is allowed to restore the vault only for
        // background tunnel startup. It must never bypass the interactive master
        // password gate: the user is asked for the master password on every launch.
        var restoredForAutoStart = Runtime.Vault.TryRestoreSession();
        if (restoredForAutoStart)
            _ = AutoStartSafelyAsync("DPAPI session restored; starting enabled tunnels behind the vault gate.");

        var gate = new VaultWindow(Runtime.Vault);
        if (gate.ShowDialog() != true)
        {
            Shutdown();
            return;
        }

        // On a machine where the DPAPI session could not be restored, the vault only
        // becomes available after the user unlocks it. Start runnable tunnels now.
        if (!restoredForAutoStart)
            _ = AutoStartSafelyAsync("Vault unlocked; starting enabled tunnels.");

        var main = new MainWindow(Runtime);
        MainWindow = main;
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        SessionEnding += (_, _) => main.AllowApplicationExit();
        main.Show();
    }

    private static async Task AutoStartSafelyAsync(string reason)
    {
        try
        {
            Runtime.Log($"[AUTO-START] {reason}");
            await Runtime.AutoStartAsync();
        }
        catch (Exception ex)
        {
            Runtime.Log($"[AUTO-START] Startup pass failed: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Runtime?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
