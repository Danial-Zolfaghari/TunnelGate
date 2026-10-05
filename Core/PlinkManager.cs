using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using TunnelGate.Models;

namespace TunnelGate.Core;

public sealed class PlinkManager
{
    private const string PuttySessionPrefix = "TunnelGateRT";
    private const string DownloadUrl = "https://the.earth.li/~sgtatham/putty/0.85/w64/plink.exe";
    private readonly ConcurrentDictionary<string, string[]> _hostKeyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;

    public PlinkManager(Action<string> log) => _log = log;

    public bool IsReady => File.Exists(AppPaths.PlinkPath) && new FileInfo(AppPaths.PlinkPath).Length > 100_000;
    public string Path => IsReady ? AppPaths.PlinkPath : "";

    public async Task<string> EnsureAsync(CancellationToken ct = default)
    {
        if (IsReady) return AppPaths.PlinkPath;
        Directory.CreateDirectory(AppPaths.ToolsDir);

        var asm = typeof(PlinkManager).Assembly;
        await using (var resource = asm.GetManifestResourceStream("TunnelGate.tools.plink.exe"))
        {
            if (resource is not null)
            {
                var tmp = AppPaths.PlinkPath + ".extract";
                await using (var fs = File.Create(tmp)) await resource.CopyToAsync(fs, ct);
                if (new FileInfo(tmp).Length > 100_000)
                {
                    File.Move(tmp, AppPaths.PlinkPath, true);
                    _log($"[PLINK] Extracted bundled Plink: {AppPaths.PlinkPath}");
                    return AppPaths.PlinkPath;
                }
                File.Delete(tmp);
            }
        }

        _log("[PLINK] Bundled copy unavailable; downloading official PuTTY build...");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var bytes = await client.GetByteArrayAsync(DownloadUrl, ct);
        if (bytes.Length < 100_000) throw new InvalidOperationException("Downloaded plink.exe is unexpectedly small.");
        var download = AppPaths.PlinkPath + ".download";
        await File.WriteAllBytesAsync(download, bytes, ct);
        File.Move(download, AppPaths.PlinkPath, true);
        _log($"[PLINK] Ready: {AppPaths.PlinkPath}");
        return AppPaths.PlinkPath;
    }

    public async Task<ProcessStartInfo> BuildTunnelStartInfoAsync(ProfileSettings settings, TunnelDefinition tunnel, CancellationToken ct)
    {
        var plink = await EnsureAsync(ct);
        var sessionName = ConfigurePuttySession(settings.KeepAlive);
        var psi = BaseStartInfo(plink);
        AddCommonArgs(psi, settings, tunnel, await HostKeyArgsAsync(settings, tunnel, ct), sessionName);
        psi.ArgumentList.Add("-N");
        if (tunnel.IsProxy)
        {
            var bind = string.IsNullOrWhiteSpace(tunnel.LocalHost) ? "127.0.0.1" : tunnel.LocalHost.Trim();
            psi.ArgumentList.Add("-D");
            psi.ArgumentList.Add($"{bind}:{tunnel.LocalPort}");
        }
        else
        {
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add($"{tunnel.RemoteBind}:{tunnel.RemotePort}:{TunnelDefinition.ForwardDestinationHost(tunnel.LocalHost)}:{tunnel.LocalPort}");
        }
        psi.ArgumentList.Add(tunnel.RemoteHost.Trim());
        return psi;
    }

    public async Task<ProcessStartInfo> BuildRemoteProbeStartInfoAsync(ProfileSettings settings, TunnelDefinition tunnel, CancellationToken ct)
    {
        var plink = await EnsureAsync(ct);
        var sessionName = ConfigurePuttySession(settings.KeepAlive);
        var psi = BaseStartInfo(plink);
        AddCommonArgs(psi, settings, tunnel, await HostKeyArgsAsync(settings, tunnel, ct), sessionName);
        psi.ArgumentList.Add(tunnel.RemoteHost.Trim());
        var bind = string.IsNullOrWhiteSpace(tunnel.RemoteBind) ? "127.0.0.1" : tunnel.RemoteBind.Trim();
        var shell = $"sh -c 'ss -tln 2>/dev/null | grep -E \"[[:space:]:]{tunnel.RemotePort}([^0-9]|$)\" || true; echo RT_DONE'";
        psi.ArgumentList.Add(shell);
        return psi;
    }

    public string SafeCommand(ProcessStartInfo psi)
    {
        var list = psi.ArgumentList.ToArray();
        var safe = new List<string>();
        for (var i = 0; i < list.Length; i++)
        {
            safe.Add(list[i] == "-pw" && i + 1 < list.Length ? "-pw" : Quote(list[i]));
            if (list[i] == "-pw" && i + 1 < list.Length)
            {
                safe.Add("********");
                i++;
            }
        }
        return $"{System.IO.Path.GetFileName(psi.FileName)} {string.Join(' ', safe)}";
    }

    private static ProcessStartInfo BaseStartInfo(string plink) => new()
    {
        FileName = plink,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8
    };

    private static void AddCommonArgs(ProcessStartInfo psi, ProfileSettings settings, TunnelDefinition tunnel, string[] hostKeys, string sessionName)
    {
        if (settings.KeepAlive > 0)
        {
            psi.ArgumentList.Add("-load");
            psi.ArgumentList.Add(sessionName);
        }
        psi.ArgumentList.Add("-ssh");
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add((tunnel.RemoteSshPort <= 0 ? 22 : tunnel.RemoteSshPort).ToString());
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add(tunnel.RemoteUser.Trim());
        psi.ArgumentList.Add("-batch");
        psi.ArgumentList.Add("-v");
        foreach (var hostKey in hostKeys)
        {
            psi.ArgumentList.Add("-hostkey");
            psi.ArgumentList.Add(hostKey);
        }
        if (settings.Compression) psi.ArgumentList.Add("-C");
        if (!string.IsNullOrWhiteSpace(tunnel.KeyFile))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(tunnel.KeyFile.Trim());
        }
        else if (!string.IsNullOrEmpty(tunnel.Password))
        {
            psi.ArgumentList.Add("-pw");
            psi.ArgumentList.Add(tunnel.Password);
        }
    }

    private async Task<string[]> HostKeyArgsAsync(ProfileSettings settings, TunnelDefinition tunnel, CancellationToken ct)
    {
        if (!settings.AutoStoreHostKey) return [];
        var cacheKey = $"{tunnel.RemoteHost.Trim().ToLowerInvariant()}:{tunnel.RemoteSshPort}";
        if (_hostKeyCache.TryGetValue(cacheKey, out var cached)) return cached;
        var plink = await EnsureAsync(ct);
        var psi = BaseStartInfo(plink);
        psi.ArgumentList.Add("-ssh");
        psi.ArgumentList.Add("-batch");
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add((tunnel.RemoteSshPort <= 0 ? 22 : tunnel.RemoteSshPort).ToString());
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add(tunnel.RemoteUser.Trim());
        psi.ArgumentList.Add(tunnel.RemoteHost.Trim());

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return [];
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(18), ct);
            var lines = ((await stdout) + "\n" + (await stderr)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var keys = lines.Select(x => x.Trim()).Where(x => x.StartsWith("ssh-", StringComparison.OrdinalIgnoreCase) && x.Contains("SHA256:", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
            _hostKeyCache[cacheKey] = keys;
            if (keys.Length > 0) _log($"[HOSTKEY] Pinned {keys.Length} host key fingerprint(s) for {tunnel.RemoteHost}.");
            return keys;
        }
        catch (Exception ex)
        {
            try { if (p is { HasExited: false }) p.Kill(true); } catch { }
            _log($"[HOSTKEY] Probe skipped: {ex.Message}");
            return [];
        }
        finally { p?.Dispose(); }
    }

    private static string ConfigurePuttySession(int keepAlive)
    {
        var sessionName = $"{PuttySessionPrefix}_{Math.Max(0, keepAlive)}";
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\SimonTatham\PuTTY\Sessions\{sessionName}");
        key?.SetValue("PingIntervalSecs", Math.Max(0, keepAlive), RegistryValueKind.DWord);
        key?.SetValue("TCPKeepalives", 1, RegistryValueKind.DWord);
        key?.SetValue("Protocol", "ssh", RegistryValueKind.String);
        return sessionName;
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\"", "\\\"");
        return value.Any(char.IsWhiteSpace) ? "\"" + escaped + "\"" : escaped;
    }
}
