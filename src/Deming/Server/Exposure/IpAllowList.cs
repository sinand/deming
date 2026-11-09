using System.Net;
using System.Net.Sockets;

namespace Deming.Server.Exposure;

/// <summary>
/// Set of single addresses and CIDR ranges. An empty list allows nobody: it never means "open to all".
/// IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) are compared as the IPv4 address they carry.
/// </summary>
public sealed class IpAllowList
{
    private readonly List<(IPAddress Network, int PrefixLength)> _entries;

    private IpAllowList(List<(IPAddress, int)> entries) => _entries = entries;

    public static IReadOnlyList<string> LoopbackEntries { get; } = ["127.0.0.1", "::1"];

    public int Count => _entries.Count;

    /// <summary>True when the list holds an entry that admits something other than loopback.</summary>
    public bool HasNonLoopbackEntry => _entries.Any(e => !CoversOnlyLoopback(e.Network, e.PrefixLength));

    public static IpAllowList Parse(IEnumerable<string> entries)
    {
        var list = new List<(IPAddress, int)>();
        foreach (var raw in entries)
        {
            var text = raw.Trim();
            if (text.Length == 0)
                continue;

            var slash = text.IndexOf('/');
            var addressPart = slash < 0 ? text : text[..slash];
            if (!IPAddress.TryParse(addressPart, out var address))
                throw new FormatException($"'{text}' is not a valid IP address or CIDR range.");

            address = Normalize(address);
            var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefix = max;
            if (slash >= 0)
            {
                // A mapped IPv6 CIDR (::ffff:1.2.3.4/120) is not worth supporting: keep the prefix literal.
                if (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > max)
                    throw new FormatException($"'{text}' has an invalid prefix length.");
            }

            list.Add((address, prefix));
        }

        return new IpAllowList(list);
    }

    public bool IsAllowed(IPAddress? address)
    {
        if (address is null)
            return false;

        address = Normalize(address);
        foreach (var (network, prefix) in _entries)
            if (network.AddressFamily == address.AddressFamily && Matches(network, address, prefix))
                return true;
        return false;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool Matches(IPAddress network, IPAddress candidate, int prefix)
    {
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        network.TryWriteBytes(a, out var len);
        candidate.TryWriteBytes(b, out _);

        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
            if (a[i] != b[i])
                return false;

        var rest = prefix % 8;
        if (rest == 0 || fullBytes >= len)
            return true;

        var mask = (byte)(0xFF << (8 - rest));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }

    private static bool CoversOnlyLoopback(IPAddress network, int prefix)
    {
        if (network.AddressFamily == AddressFamily.InterNetwork)
            return IPAddress.IsLoopback(network) && prefix >= 8;
        return IPAddress.IsLoopback(network) && prefix == 128;
    }
}
