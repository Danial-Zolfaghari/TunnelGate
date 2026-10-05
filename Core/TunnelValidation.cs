using System.Net;
using System.Text.RegularExpressions;
using TunnelGate.Models;

namespace TunnelGate.Core;

public static partial class TunnelValidation
{
    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)[A-Za-z0-9-]{1,63}(?<!-)(\.(?!-)[A-Za-z0-9-]{1,63}(?<!-))*$")]
    private static partial Regex HostRegex();

    public static string? Validate(ProfileSettings settings, TunnelDefinition tunnel)
    {
        if (string.IsNullOrWhiteSpace(tunnel.RemoteHost) || !ValidHost(tunnel.RemoteHost)) return "Invalid SSH host.";
        if (string.IsNullOrWhiteSpace(tunnel.RemoteUser)) return "SSH username is required.";
        if (tunnel.RemoteSshPort is < 1 or > 65535) return "SSH port must be 1-65535.";
        if (string.IsNullOrWhiteSpace(tunnel.Password) && string.IsNullOrWhiteSpace(tunnel.KeyFile)) return "Password or key file is required.";
        if (!string.IsNullOrWhiteSpace(tunnel.KeyFile) && !File.Exists(tunnel.KeyFile)) return "SSH key file does not exist.";
        if (settings.Reconnect && settings.ReconnectDelay < 1) return "Reconnect delay must be at least 1 second.";

        if (tunnel.IsProxy)
        {
            var bind = string.IsNullOrWhiteSpace(tunnel.LocalHost) ? "127.0.0.1" : tunnel.LocalHost.Trim();
            if (!ValidBind(bind)) return "Invalid local bind address.";
            if (tunnel.LocalPort is < 1 or > 65535) return "SOCKS port must be 1-65535.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(tunnel.RemoteBind) || !ValidBind(tunnel.RemoteBind)) return "Invalid remote bind address.";
        if (string.IsNullOrWhiteSpace(tunnel.LocalHost) || !ValidHost(tunnel.LocalHost)) return "Invalid local destination host.";
        if (tunnel.RemotePort is < 1 or > 65535 || tunnel.LocalPort is < 1 or > 65535) return "Tunnel ports must be 1-65535.";
        return null;
    }

    public static bool ValidHost(string value)
    {
        var host = (value ?? "").Trim().Trim('[', ']');
        if (host.Length is 0 or > 253) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out _)) return true;
        return HostRegex().IsMatch(host);
    }

    public static bool ValidBind(string value)
    {
        var host = (value ?? "").Trim();
        return host is "0.0.0.0" or "127.0.0.1" or "::" or "::1" || ValidHost(host);
    }
}
