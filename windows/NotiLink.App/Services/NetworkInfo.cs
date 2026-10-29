using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NotiLink.App.Services;

internal static class NetworkInfo
{
    // VPNs / virtual switches: a phone on the same Wi-Fi (or whose hotspot this PC joined) can't
    // reach the PC through these, so they go last instead of being shown as "the" IP.
    private static readonly string[] VirtualHints = { "vethernet", "virtualbox", "vmware", "hyper-v", "tailscale", "zerotier", "wsl", "loopback", "bluetooth" };

    /// <summary>Every usable IPv4 of this PC, best first: real adapters with a gateway (the Wi-Fi
    /// or Ethernet the phone is on, including a phone's hotspot), then the rest.</summary>
    public static IReadOnlyList<(string Ip, string Adapter)> GetLocalIPv4s()
    {
        var list = new List<(string Ip, string Adapter, int Rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                var isVirtual = VirtualHints.Any(name.Contains);
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.") || ip.StartsWith("127.")) continue;
                    list.Add((ip, ni.Name, (isVirtual ? 2 : 0) + (hasGateway ? 0 : 1)));
                }
            }
        }
        catch (Exception) { }
        return list.OrderBy(x => x.Rank).Select(x => (x.Ip, x.Adapter)).ToList();
    }

    // Overlay VPNs a phone can reach this PC through from anywhere (e.g. on mobile data).
    private static readonly string[] VpnHints = { "tailscale", "zerotier" };
    private static readonly string[] SkipHints = { "vethernet", "virtualbox", "vmware", "hyper-v", "wsl", "loopback", "bluetooth" };

    private static bool IsCgnat(string ip)
    {
        var p = ip.Split('.');
        return p.Length == 4 && p[0] == "100" && int.TryParse(p[1], out var b) && b is >= 64 and <= 127;
    }

    /// <summary>What the phone should know to find this PC again on another network:
    /// kind "lan" (Wi-Fi/Ethernet/hotspot) or "vpn" (Tailscale/ZeroTier, works from anywhere).
    /// Virtual adapters (Hyper-V, WSL…) are left out.</summary>
    public static IReadOnlyList<(string Ip, string Kind, string Adapter)> GetAddressesForPhone()
    {
        var list = new List<(string, string, string)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                var isVpn = VpnHints.Any(name.Contains);
                if (!isVpn && SkipHints.Any(name.Contains)) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.") || ip.StartsWith("127.")) continue;
                    list.Add((ip, isVpn || IsCgnat(ip) ? "vpn" : "lan", ni.Name));
                }
            }
        }
        catch (Exception) { }
        return list;
    }

    public static string GetLocalIPv4()
    {
        var best = GetLocalIPv4s().FirstOrDefault();
        if (best.Ip != null) return best.Ip;
        foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return address.ToString();
        }
        return "127.0.0.1";
    }
}
