using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TruckersMP.Proxy;

internal static class LatencyProbe
{
    private const SocketOptionName IpUnicastInterface = (SocketOptionName)31;
    private static readonly IPEndPoint TestEndpoint = new(IPAddress.Parse("1.1.1.1"), 80);
    private static readonly byte[] HttpProbeBytes = "HEAD /generate_204 HTTP/1.1\r\nHost: cp.cloudflare.com\r\nConnection: close\r\n\r\n"u8.ToArray();

    public static async Task<TimeSpan> MeasureAsync(
        string interfaceName,
        CancellationToken cancellationToken,
        string? customEndpoint = null)
    {
        var targetEndpoint = TestEndpoint;
        if (!string.IsNullOrWhiteSpace(customEndpoint) &&
            IPEndPoint.TryParse(customEndpoint, out var parsed))
        {
            targetEndpoint = parsed;
        }

        // Enumerating adapters and reading their IP properties are blocking IP Helper calls, and
        // this runs on the UI thread's synchronisation context, so resolve them on a worker.
        var (interfaceIndex, localAddress) = await Task.Run(() =>
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(candidate =>
                candidate.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase) &&
                candidate.OperationalStatus == OperationalStatus.Up)
                ?? throw new InvalidOperationException($"{interfaceName} is not ready");

            var properties = adapter.GetIPProperties();
            var ipv4 = properties.GetIPv4Properties()
                ?? throw new InvalidOperationException($"{interfaceName} has no IPv4 properties");
            var address = properties.UnicastAddresses
                .Select(unicast => unicast.Address)
                .FirstOrDefault(unicast => unicast.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new InvalidOperationException($"{interfaceName} has no IPv4 address");
            return (ipv4.Index, address);
        }, cancellationToken);

        var samples = new List<TimeSpan>(3);
        var buffer = new byte[256];

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ReceiveTimeout = 6000;
            socket.SendTimeout = 6000;
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                IpUnicastInterface,
                IPAddress.HostToNetworkOrder(interfaceIndex));
            socket.Bind(new IPEndPoint(localAddress, 0));

            var connectWatch = Stopwatch.StartNew();
            await socket.ConnectAsync(targetEndpoint, cancellationToken);
            connectWatch.Stop();

            // When Hybrid TCP Lane is enabled, mqvpn's local lwIP stack intercepts the TCP SYN locally
            // and replies with SYN-ACK in < 1ms without sending any packets through the QUIC tunnel.
            // If the handshake completed in < 15ms, we must send application data to force the TCP lane
            // to open a QUIC stream and measure true end-to-end network RTT.
            if (connectWatch.ElapsedMilliseconds < 15)
            {
                var requestWatch = Stopwatch.StartNew();
                await socket.SendAsync(HttpProbeBytes, SocketFlags.None, cancellationToken);
                var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                requestWatch.Stop();

                if (received <= 0)
                    throw new InvalidOperationException("Empty response received from probe endpoint");

                samples.Add(requestWatch.Elapsed);
            }
            else
            {
                // In standard RAW mode, the TCP SYN traversed the entire tunnel, so connect time is the true 1-RTT.
                samples.Add(connectWatch.Elapsed);
            }

            if (attempt < 2)
                await Task.Delay(80, cancellationToken);
        }

        return samples.Order().ElementAt(1);
    }
}
