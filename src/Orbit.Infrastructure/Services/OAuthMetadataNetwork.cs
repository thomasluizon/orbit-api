using System.Net;
using System.Net.Sockets;

namespace Orbit.Infrastructure.Services;

public static class OAuthMetadataNetwork
{
    private static readonly IPNetwork[] BlockedNetworks = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15",
        "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3", "2001::/23", "2001:db8::/32",
        "2002::/16", "3fff::/20"
    }.Select(IPNetwork.Parse).ToArray();
    private static readonly IPNetwork GlobalIpv6 = IPNetwork.Parse("2000::/3");
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = ConnectAsync
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolvePublicAddressesAsync(context.DnsEndPoint.Host, Dns.GetHostAddressesAsync, ct);
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("Unable to connect to the client metadata host.");
    }

    internal static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var addresses = await resolve(host, ct);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Client metadata requires public IP addresses.");
        return addresses;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return (address.AddressFamily == AddressFamily.InterNetwork
                || address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 && GlobalIpv6.Contains(address))
            && !BlockedNetworks.Any(network => network.Contains(address));
    }
}
