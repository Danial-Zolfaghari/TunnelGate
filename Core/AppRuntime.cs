using System.Text;
using TunnelGate.Models;

namespace TunnelGate.Core;

public sealed class AppRuntime : IDisposable
{
    private readonly object _logGate = new();
    public SecureVault Vault { get; } = new();
    public TunnelEngine Engine { get; }
    public event Action<string>? LogReceived;

    public AppRuntime()
    {
        Engine = new TunnelEngine(Log);
    }

    public void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_logGate)
        {
            try { File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
        LogReceived?.Invoke(line);
    }

    public async Task AutoStartAsync()
    {
        var vault = Vault.Current;
        if (vault is null) return;

        // TunnelGate is designed to bring enabled configurations online as soon as
        // the vault is available. Persist this preference so older vaults created
        // with auto-start disabled are migrated to the new behavior automatically.
        if (!vault.AutoStartTunnels)
        {
            vault.AutoStartTunnels = true;
            try { Vault.Save(); } catch { }
        }

        Log("[AUTO-START] Starting enabled tunnel configurations...");
        await Engine.StartAllRunnableProfilesAsync(vault);
    }

    public void Dispose()
    {
        Engine.Dispose();
    }
}
