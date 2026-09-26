using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using EasyBalance.Shared;

namespace EasyBalance.Service.Health;

public interface IInterfaceProbe
{
    Task<TimeSpan?> ProbeAsync(NetworkAdapterInfo adapter, AddressFamilyKind family, Uri endpoint,
        TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>HTTPS probe with both a source address and Windows IP_UNICAST_IF selected.</summary>
public sealed class BoundInterfaceProbe : IInterfaceProbe
{
    public async Task<TimeSpan?> ProbeAsync(NetworkAdapterInfo adapter, AddressFamilyKind family,
        Uri endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps) return null;
        var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(value => Guid.TryParse(value.Id, out var current) &&
                Guid.TryParse(adapter.Id, out var requested) ? current == requested :
                string.Equals(value.Id, adapter.Id, StringComparison.OrdinalIgnoreCase));
        if (networkInterface is null) return null;
        var addressFamily = family == AddressFamilyKind.IPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        var source = networkInterface.GetIPProperties().UnicastAddresses
            .Select(value => value.Address)
            .FirstOrDefault(value => value.AddressFamily == addressFamily &&
                !IPAddress.IsLoopback(value) && !value.IsIPv6LinkLocal);
        if (source is null) return null;
        var index = family == AddressFamilyKind.IPv4
            ? networkInterface.GetIPProperties().GetIPv4Properties()?.Index
            : networkInterface.GetIPProperties().GetIPv6Properties()?.Index;
        if (index is null) return null;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var destinations = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, addressFamily, timeoutSource.Token);
            var destination = destinations.FirstOrDefault();
            if (destination is null) return null;
            using var socket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
            // IP_UNICAST_IF / IPV6_UNICAST_IF are option 31 on Windows.
            socket.SetSocketOption(addressFamily == AddressFamily.InterNetwork ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
                (SocketOptionName)31,
                addressFamily == AddressFamily.InterNetwork ? IPAddress.HostToNetworkOrder(index.Value) : index.Value);
            socket.Bind(new IPEndPoint(source, 0));
            var stopwatch = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(destination, endpoint.Port), timeoutSource.Token);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(stream, false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.DnsSafeHost
            }, timeoutSource.Token);
            var path = string.IsNullOrEmpty(endpoint.PathAndQuery) ? "/" : endpoint.PathAndQuery;
            var request = Encoding.ASCII.GetBytes($"HEAD {path} HTTP/1.1\r\nHost: {endpoint.DnsSafeHost}\r\nConnection: close\r\n\r\n");
            await tls.WriteAsync(request, timeoutSource.Token);
            var buffer = new byte[128];
            var count = await tls.ReadAsync(buffer, timeoutSource.Token);
            stopwatch.Stop();
            var statusLine = Encoding.ASCII.GetString(buffer, 0, count);
            return statusLine.StartsWith("HTTP/", StringComparison.Ordinal) &&
                statusLine.Length >= 12 && statusLine[9] is >= '2' and <= '4'
                ? stopwatch.Elapsed : null;
        }
        catch (Exception exception) when (exception is SocketException or IOException or AuthenticationException or
            OperationCanceledException)
        {
            return null;
        }
    }
}
