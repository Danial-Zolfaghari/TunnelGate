using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TunnelGate.Models;

namespace TunnelGate.Core;

public static class NetworkTools
{
    public static List<NetworkAdapterInfo> ListAdapters()
    {
        var list = new List<NetworkAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus != OperationalStatus.Down))
        {
            try
            {
                var props = nic.GetIPProperties();
                list.Add(new NetworkAdapterInfo
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
                    Status = nic.OperationalStatus.ToString(),
                    IpAddresses = props.UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !x.Address.ToString().StartsWith("169.254.")).Select(x => x.Address.ToString()).ToList(),
                    DnsServers = props.DnsAddresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.ToString()).ToList()
                });
            }
            catch { }
        }
        return list.OrderBy(x => x.Name).ToList();
    }

    public static async Task SetDnsAsync(string adapter, string primary, string? secondary)
    {
        if (!System.Net.IPAddress.TryParse(primary, out var p) || p.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("Invalid primary IPv4 DNS address.");
        if (!string.IsNullOrWhiteSpace(secondary) && (!System.Net.IPAddress.TryParse(secondary, out var s) || s.AddressFamily != AddressFamily.InterNetwork))
            throw new InvalidOperationException("Invalid secondary IPv4 DNS address.");

        await RunNetshAsync("interface", "ipv4", "set", "dnsservers", $"name={adapter}", "source=static", $"address={primary}", "validate=no");
        if (!string.IsNullOrWhiteSpace(secondary))
            await RunNetshAsync("interface", "ipv4", "add", "dnsservers", $"name={adapter}", $"address={secondary}", "index=2", "validate=no");
    }

    public static Task ResetDnsAsync(string adapter)
        => RunNetshAsync("interface", "ipv4", "set", "dnsservers", $"name={adapter}", "source=dhcp");

    private static async Task RunNetshAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("netsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start netsh.");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var output = (await so) + "\n" + (await se);
        if (p.ExitCode != 0) throw new InvalidOperationException(output.Trim());
    }
}
