using System.Text.Json.Serialization;

namespace TunnelGate.Models;

public sealed class TunnelDefinition
{
    public string Uid { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "reverse";
    public string RemoteHost { get; set; } = "";
    public int RemoteSshPort { get; set; } = 22;
    public string RemoteUser { get; set; } = "";
    public string Password { get; set; } = "";
    public bool SavePassword { get; set; }
    public string KeyFile { get; set; } = "";
    public string RemoteBind { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; }
    public string LocalHost { get; set; } = "127.0.0.1";
    public int LocalPort { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AutoRestart { get; set; }
    public int AutoRestartDays { get; set; }
    public int AutoRestartHours { get; set; }
    public int AutoRestartMinutes { get; set; } = 60;

    [JsonIgnore] public bool IsProxy => string.Equals(Mode, "proxy", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (IsProxy ? $"SOCKS {LocalHost}:{LocalPort}" : $"{RemoteBind}:{RemotePort}") : Name.Trim();
    [JsonIgnore] public string Route => IsProxy ? $"SOCKS {LocalHost}:{LocalPort}" : $"{RemoteBind}:{RemotePort} → {ForwardDestinationHost(LocalHost)}:{LocalPort}";

    public long AutoRestartIntervalSeconds()
    {
        if (!AutoRestart) return 0;
        return Math.Max(0, AutoRestartDays) * 86400L + Math.Max(0, AutoRestartHours) * 3600L + Math.Max(0, AutoRestartMinutes) * 60L;
    }

    public static string ForwardDestinationHost(string value)
    {
        var host = (value ?? "").Trim();
        return string.IsNullOrEmpty(host) || host is "0.0.0.0" or "::" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : host;
    }

    public TunnelDefinition Clone() => (TunnelDefinition)MemberwiseClone();
}

public sealed class ProfileSettings
{
    public string Language { get; set; } = "en";
    public bool Reconnect { get; set; } = true;
    public int ReconnectDelay { get; set; } = 5;
    public int KeepAlive { get; set; } = 30;
    public bool Compression { get; set; }
    public bool AutoStoreHostKey { get; set; }
    public List<TunnelDefinition> Tunnels { get; set; } = [];
}

public sealed class VaultCategory
{
    public string Uid { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class VaultProfile
{
    public string Uid { get; set; } = Guid.NewGuid().ToString("N");
    public string CategoryUid { get; set; } = "";
    public string Name { get; set; } = "Default";
    public bool Enabled { get; set; } = true;
    public ProfileSettings Settings { get; set; } = new();
}

public sealed class VaultData
{
    public string Language { get; set; } = "en";
    public string ActiveProfileUid { get; set; } = "";
    public List<VaultCategory> Categories { get; set; } = [];
    public List<VaultProfile> Profiles { get; set; } = [];
    public bool AutoStartTunnels { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = true;

    public VaultProfile? ActiveProfile => Profiles.FirstOrDefault(p => p.Uid == ActiveProfileUid);

    public static VaultData CreateDefault()
    {
        var category = new VaultCategory { Name = "Default", SortOrder = 0 };
        var profile = new VaultProfile { Name = "Default", CategoryUid = category.Uid };
        return new VaultData
        {
            Categories = [category],
            Profiles = [profile],
            ActiveProfileUid = profile.Uid
        };
    }
}

public sealed class NetworkAdapterInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> IpAddresses { get; set; } = [];
    public List<string> DnsServers { get; set; } = [];

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public enum EngineStatus
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Stopped,
    Error
}
