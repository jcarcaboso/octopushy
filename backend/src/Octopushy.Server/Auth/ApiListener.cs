using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Octopushy.Server.Auth;

/// <summary>The addresses the API port listens on, and the networks its proxy may forward from.</summary>
public static class ApiListener
{
    /// <summary>
    /// An address, or a host name resolved once at start. Compose names the backend's address on the
    /// frontend's network this way, so the API port stays off the agents network.
    /// </summary>
    public static IPAddress[] Resolve(string bindAddress)
    {
        if (IPAddress.TryParse(bindAddress, out var address))
        {
            return [address];
        }

        var addresses = Dns.GetHostAddresses(bindAddress);
        return addresses.Length > 0 ? addresses : throw new InvalidOperationException($"The API bind address {bindAddress} resolves to no address.");
    }

    /// <summary>
    /// Where forwarded headers are honoured from: the configured networks, or else the networks of this machine's
    /// interfaces that the API listens on. A client elsewhere cannot name its own address.
    /// </summary>
    public static IReadOnlyList<IPNetwork> TrustedNetworks(IReadOnlyList<string> configured, IReadOnlyList<IPAddress> listening,
        IEnumerable<(IPAddress Address, int PrefixLength)> interfaces)
    {
        if (configured.Count > 0)
        {
            return configured.Select(entry => entry.Contains('/') ? IPNetwork.Parse(entry)
                : Network(IPAddress.Parse(entry), IPAddress.Parse(entry).AddressFamily == AddressFamily.InterNetwork ? 32 : 128)).ToList();
        }

        return interfaces
            .Where(entry => listening.Any(address => address.Equals(entry.Address)
                || (address.Equals(IPAddress.Any) && entry.Address.AddressFamily == AddressFamily.InterNetwork)
                || address.Equals(IPAddress.IPv6Any)))
            .Select(entry => Network(entry.Address, entry.PrefixLength))
            .Distinct()
            .ToList();
    }

    public static IEnumerable<(IPAddress Address, int PrefixLength)> Interfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => (unicast.Address, unicast.PrefixLength));

    private static IPNetwork Network(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = prefixLength; bit < bytes.Length * 8; bit++)
        {
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        }

        return new IPNetwork(new IPAddress(bytes), prefixLength);
    }
}
