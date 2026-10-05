using System.Diagnostics;
using System.Net.Sockets;
using TunnelGate.Models;

namespace TunnelGate.Core;

internal sealed class TunnelWorker : IDisposable
{
    private static readonly string[] ErrorMarkers =
    [
        "fatal error", "remote port forwarding failed", "access denied", "cannot assign requested address",
        "administratively prohibited", "open failed", "connection refused", "network error",
        "unable to open connection", "disconnected"
    ];

    private readonly ProfileSettings _settings;
    private readonly TunnelDefinition _tunnel;
    private readonly PlinkManager _plink;
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private Task? _runTask;
    private int _consecutiveFailures;
    private int _healthFailures;

    public string Uid => _tunnel.Uid;
    public bool Live { get; private set; }
    public bool Running => _runTask is { IsCompleted: false };

    public event Action<string>? Log;
    public event Action<string, bool>? LiveChanged;
    public event Action? StateChanged;

    public TunnelWorker(ProfileSettings settings, TunnelDefinition tunnel, PlinkManager plink)
    {
        _settings = CloneSettings(settings);
        _tunnel = tunnel.Clone();
        _plink = plink;
    }

    public void Start()
    {
        if (Running) return;
        _runTask = Task.Run(RunLoopAsync);
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        SetLive(false);
        KillProcess();
        if (_runTask is not null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
        }
        StateChanged?.Invoke();
    }

    private async Task RunLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var outcome = await RunOnceAsync(_stop.Token);
            if (_stop.IsCancellationRequested) break;
            if (outcome == RunOutcome.Stopped) break;
            if (outcome == RunOutcome.ScheduledRestart)
            {
                Write($"[AUTO-RESTART] {_tunnel.DisplayName}: restarting now.");
                _consecutiveFailures = 0;
                continue;
            }
            if (!_settings.Reconnect)
            {
                Write($"[CONNECT] {_tunnel.DisplayName}: connection closed; reconnect disabled.");
                break;
            }
            _consecutiveFailures++;
            var baseDelay = Math.Max(1, _settings.ReconnectDelay);
            var delay = Math.Min(30, baseDelay * (int)Math.Pow(2, Math.Min(3, _consecutiveFailures - 1)));
            Write($"[RECONNECT] {_tunnel.DisplayName}: retrying in {delay}s.");
            StateChanged?.Invoke();
            try { await Task.Delay(TimeSpan.FromSeconds(delay), _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
        SetLive(false);
        StateChanged?.Invoke();
    }

    private async Task<RunOutcome> RunOnceAsync(CancellationToken ct)
    {
        var validation = TunnelValidation.Validate(_settings, _tunnel);
        if (validation is not null)
        {
            Write($"[ERROR] {_tunnel.DisplayName}: {validation}");
            return RunOutcome.Failed;
        }
        if (_tunnel.IsProxy)
        {
            var portError = PortManager.ValidateProxyPort(_tunnel.LocalPort);
            if (portError is not null)
            {
                Write($"[ERROR] {_tunnel.DisplayName}: {portError}");
                return RunOutcome.Failed;
            }
        }

        ProcessStartInfo psi;
        try { psi = await _plink.BuildTunnelStartInfoAsync(_settings, _tunnel, ct); }
        catch (Exception ex)
        {
            Write($"[PLINK] {_tunnel.DisplayName}: {ex.Message}");
            return RunOutcome.Failed;
        }

        Write($"[CONNECT] {_tunnel.DisplayName}");
        Write(_plink.SafeCommand(psi));
        if (_tunnel.IsProxy)
            Write($"[ROUTE] SOCKS {_tunnel.LocalHost}:{_tunnel.LocalPort} via {_tunnel.RemoteUser}@{_tunnel.RemoteHost}:{_tunnel.RemoteSshPort}");
        else
            Write($"[ROUTE] {_tunnel.RemoteBind}:{_tunnel.RemotePort} → {TunnelDefinition.ForwardDestinationHost(_tunnel.LocalHost)}:{_tunnel.LocalPort} via {_tunnel.RemoteUser}@{_tunnel.RemoteHost}:{_tunnel.RemoteSshPort}");

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += (_, _) => exited.TrySetResult(SafeExitCode(_process));
            _process.OutputDataReceived += (_, e) => HandleLine(e.Data, ready, failed);
            _process.ErrorDataReceived += (_, e) => HandleLine(e.Data, ready, failed);
            if (!_process.Start()) throw new InvalidOperationException("Failed to start plink.exe.");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Write($"[PLINK] Failed to start: {ex.Message}");
            CleanupProcess();
            return RunOutcome.Failed;
        }

        try
        {
            var grace = Task.Delay(TimeSpan.FromSeconds(4), ct);
            var timeout = Task.Delay(TimeSpan.FromSeconds(10), ct);
            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(ready.Task, failed.Task, exited.Task, grace, timeout);
                if (completed == failed.Task)
                {
                    Write($"[ERROR] {_tunnel.DisplayName}: {await failed.Task}");
                    KillProcess();
                    CleanupProcess();
                    return RunOutcome.Failed;
                }
                if (completed == exited.Task)
                {
                    Write($"[ERROR] {_tunnel.DisplayName}: Plink exited (code {await exited.Task}).");
                    CleanupProcess();
                    return RunOutcome.Failed;
                }
                if (completed == ready.Task)
                {
                    if (_process is null || _process.HasExited) { CleanupProcess(); return RunOutcome.Failed; }
                    break;
                }
                if (completed == grace)
                {
                    if (_process is null || _process.HasExited) { CleanupProcess(); return RunOutcome.Failed; }
                    if (_tunnel.IsProxy && PortManager.IsTcpListening(_tunnel.LocalPort)) break;
                    grace = Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    continue;
                }
                if (completed == timeout)
                {
                    Write($"[ERROR] {_tunnel.DisplayName}: connection timeout.");
                    KillProcess();
                    CleanupProcess();
                    return RunOutcome.Failed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            KillProcess();
            CleanupProcess();
            return RunOutcome.Stopped;
        }

        _consecutiveFailures = 0;
        SetLive(true);
        Write($"[LIVE] {_tunnel.DisplayName}: tunnel active.");
        _ = Task.Run(() => VerifyAsync(ct), ct);

        var health = HealthWatchdogAsync(ct);
        Task? scheduled = null;
        var interval = _tunnel.AutoRestartIntervalSeconds();
        if (interval >= 60) scheduled = Task.Delay(TimeSpan.FromSeconds(interval), ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tasks = new List<Task> { exited.Task, health };
                if (scheduled is not null) tasks.Add(scheduled);
                var done = await Task.WhenAny(tasks);
                if (scheduled is not null && done == scheduled)
                {
                    SetLive(false);
                    KillProcess();
                    return RunOutcome.ScheduledRestart;
                }
                if (done == health)
                {
                    var ok = await health;
                    if (!ok)
                    {
                        _healthFailures++;
                        Write($"[HEALTH] {_tunnel.DisplayName}: check failed ({_healthFailures}/2).");
                        if (_healthFailures >= 2)
                        {
                            SetLive(false);
                            Write($"[HEALTH] {_tunnel.DisplayName}: consecutive health failures; reconnecting.");
                            KillProcess();
                            return RunOutcome.Failed;
                        }
                    }
                    else _healthFailures = 0;
                    health = HealthWatchdogAsync(ct);
                    continue;
                }
                if (done == exited.Task)
                {
                    var code = await exited.Task;
                    SetLive(false);
                    Write($"[EXIT] {_tunnel.DisplayName}: Plink exited with code {code}.");
                    return code == 0 ? RunOutcome.Stopped : RunOutcome.Failed;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { SetLive(false); KillProcess(); CleanupProcess(); }
        return RunOutcome.Stopped;
    }

    private void HandleLine(string? line, TaskCompletionSource<bool> ready, TaskCompletionSource<string> failed)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var text = line.Trim();
        Write(text);
        var lower = text.ToLowerInvariant();
        if (ErrorMarkers.Any(lower.Contains)) failed.TrySetResult(text);
        if (lower.Contains("access granted") || lower.Contains("authentication succeeded") || lower.Contains("authenticated to") || lower.Contains("session opened")) ready.TrySetResult(true);
    }

    private async Task<bool> HealthWatchdogAsync(CancellationToken ct)
    {
        var seconds = Math.Max(60, Math.Max(0, _settings.KeepAlive) * 2);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        if (_process is null || _process.HasExited) return false;
        if (_tunnel.IsProxy) return PortManager.IsTcpListening(_tunnel.LocalPort);
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(_tunnel.RemoteHost, _tunnel.RemoteSshPort <= 0 ? 22 : _tunnel.RemoteSshPort, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    private async Task VerifyAsync(CancellationToken ct)
    {
        try
        {
            if (_tunnel.IsProxy)
            {
                await Task.Delay(450, ct);
                Write(PortManager.IsTcpListening(_tunnel.LocalPort)
                    ? $"[VERIFY] Local SOCKS {_tunnel.LocalHost}:{_tunnel.LocalPort} is listening."
                    : $"[WARN] SOCKS {_tunnel.LocalHost}:{_tunnel.LocalPort} is not visible as listening yet.");
                return;
            }

            var local = TunnelDefinition.ForwardDestinationHost(_tunnel.LocalHost);
            Write($"[VERIFY] Reverse {_tunnel.RemoteBind}:{_tunnel.RemotePort} → {local}:{_tunnel.LocalPort}");
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(local, _tunnel.LocalPort, timeout.Token);
                Write($"[VERIFY] Local destination {local}:{_tunnel.LocalPort} accepts TCP.");
            }
            catch { Write($"[WARN] Local destination {local}:{_tunnel.LocalPort} is not accepting TCP; SSH tunnel remains connected."); }

            var psi = await _plink.BuildRemoteProbeStartInfoAsync(_settings, _tunnel, ct);
            using var p = Process.Start(psi);
            if (p is null) return;
            var so = p.StandardOutput.ReadToEndAsync(ct);
            var se = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
            var output = (await so) + "\n" + (await se);
            var listening = output.Split('\n').Any(line => line.Contains($":{_tunnel.RemotePort}", StringComparison.Ordinal));
            Write(listening
                ? $"[VERIFY] Remote port {_tunnel.RemoteBind}:{_tunnel.RemotePort} is listening on SSH server."
                : $"[WARN] Remote port {_tunnel.RemoteBind}:{_tunnel.RemotePort} was not confirmed by ss; check GatewayPorts/server bind.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Write($"[WARN] Verify failed: {ex.Message}"); }
    }

    private void SetLive(bool value)
    {
        if (Live == value) return;
        Live = value;
        LiveChanged?.Invoke(Uid, value);
        StateChanged?.Invoke();
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(true);
                _process.WaitForExit(2500);
            }
        }
        catch { }
    }

    private void CleanupProcess()
    {
        try { _process?.Dispose(); } catch { }
        _process = null;
    }

    private void Write(string message) => Log?.Invoke(message);
    private static int SafeExitCode(Process? p) { try { return p?.ExitCode ?? -1; } catch { return -1; } }

    private static ProfileSettings CloneSettings(ProfileSettings s) => new()
    {
        Language = s.Language,
        Reconnect = s.Reconnect,
        ReconnectDelay = s.ReconnectDelay,
        KeepAlive = s.KeepAlive,
        Compression = s.Compression,
        AutoStoreHostKey = s.AutoStoreHostKey,
        Tunnels = []
    };

    public void Dispose()
    {
        _stop.Cancel();
        KillProcess();
        CleanupProcess();
        _stop.Dispose();
    }

    private enum RunOutcome { Stopped, Failed, ScheduledRestart }
}
