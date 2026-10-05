using System.Collections.Concurrent;
using TunnelGate.Models;

namespace TunnelGate.Core;

public sealed class TunnelEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, TunnelWorker> _workers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PlinkManager _plink;
    private readonly Action<string> _log;

    public event Action<EngineStatus>? StatusChanged;
    public event Action<string, bool>? TunnelStateChanged;

    public TunnelEngine(Action<string> log)
    {
        _log = log;
        _plink = new PlinkManager(log);
        PortManager.CleanupStalePlink(log);
    }

    public bool AnyRunning => _workers.Values.Any(w => w.Running);
    public bool AnyLive => _workers.Values.Any(w => w.Live);
    public IReadOnlyDictionary<string, bool> LiveStates => _workers.ToDictionary(kv => kv.Key, kv => kv.Value.Live);
    public string PlinkPath => _plink.Path;

    public async Task StartProfileAsync(VaultProfile profile)
    {
        await _gate.WaitAsync();
        try
        {
            StatusChanged?.Invoke(EngineStatus.Connecting);
            var active = profile.Settings.Tunnels.Where(t => t.Enabled).ToList();
            if (active.Count == 0) throw new InvalidOperationException("No enabled tunnels in this profile.");
            foreach (var tunnel in active)
            {
                var validation = TunnelValidation.Validate(profile.Settings, tunnel);
                if (validation is not null) throw new InvalidOperationException($"{tunnel.DisplayName}: {validation}");
                if (_workers.TryGetValue(tunnel.Uid, out var existing) && existing.Running) continue;
                if (_workers.TryRemove(tunnel.Uid, out var old)) old.Dispose();
                var worker = new TunnelWorker(profile.Settings, tunnel, _plink);
                Hook(worker);
                _workers[tunnel.Uid] = worker;
                worker.Start();
            }
            RecomputeStatus();
        }
        finally { _gate.Release(); }
    }

    public async Task StartAllRunnableProfilesAsync(VaultData vault)
    {
        foreach (var profile in vault.Profiles)
        {
            var category = vault.Categories.FirstOrDefault(c => c.Uid == profile.CategoryUid);
            if (!profile.Enabled || category is { Enabled: false }) continue;

            foreach (var tunnel in profile.Settings.Tunnels.Where(t => t.Enabled))
            {
                // Start each tunnel in isolation. One invalid or unavailable tunnel
                // must never prevent the rest of the enabled configuration from starting.
                var isolatedSettings = new ProfileSettings
                {
                    Language = profile.Settings.Language,
                    Reconnect = profile.Settings.Reconnect,
                    ReconnectDelay = profile.Settings.ReconnectDelay,
                    KeepAlive = profile.Settings.KeepAlive,
                    Compression = profile.Settings.Compression,
                    AutoStoreHostKey = profile.Settings.AutoStoreHostKey,
                    Tunnels = [tunnel]
                };
                var isolatedProfile = new VaultProfile
                {
                    Uid = profile.Uid,
                    CategoryUid = profile.CategoryUid,
                    Name = profile.Name,
                    Enabled = true,
                    Settings = isolatedSettings
                };

                try
                {
                    await StartProfileAsync(isolatedProfile);
                    _log($"[AUTO-START] {profile.Name} / {tunnel.DisplayName}: started.");
                }
                catch (Exception ex)
                {
                    _log($"[AUTO-START] {profile.Name} / {tunnel.DisplayName}: {ex.Message}");
                }
            }
        }
    }

    public async Task StopTunnelAsync(string uid)
    {
        if (_workers.TryRemove(uid, out var worker))
        {
            await worker.StopAsync();
            worker.Dispose();
        }
        RecomputeStatus();
    }

    public async Task RestartTunnelAsync(ProfileSettings settings, TunnelDefinition tunnel)
    {
        await StopTunnelAsync(tunnel.Uid);
        var isolated = new ProfileSettings
        {
            Language = settings.Language,
            Reconnect = settings.Reconnect,
            ReconnectDelay = settings.ReconnectDelay,
            KeepAlive = settings.KeepAlive,
            Compression = settings.Compression,
            AutoStoreHostKey = settings.AutoStoreHostKey,
            Tunnels = [tunnel]
        };
        var p = new VaultProfile { Settings = isolated, Enabled = true };
        await StartProfileAsync(p);
    }

    public async Task StopAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var workers = _workers.Values.ToArray();
            _workers.Clear();
            await Task.WhenAll(workers.Select(w => w.StopAsync()));
            foreach (var w in workers) w.Dispose();
            StatusChanged?.Invoke(EngineStatus.Stopped);
            _log("[STOP] All tunnels stopped.");
        }
        finally { _gate.Release(); }
    }

    private void Hook(TunnelWorker worker)
    {
        worker.Log += msg => _log(msg);
        worker.LiveChanged += (uid, live) =>
        {
            TunnelStateChanged?.Invoke(uid, live);
            RecomputeStatus();
        };
        worker.StateChanged += RecomputeStatus;
    }

    private void RecomputeStatus()
    {
        var status = AnyLive ? EngineStatus.Connected : AnyRunning ? EngineStatus.Connecting : EngineStatus.Idle;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        try { StopAllAsync().GetAwaiter().GetResult(); } catch { }
        _gate.Dispose();
    }
}
