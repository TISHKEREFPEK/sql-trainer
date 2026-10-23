using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Classroom.Contracts;

namespace Classroom.Server;

public sealed class PairingDiscovery(PairingAnnouncement announcement, IPAddress listenAddress) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, PairingCode.DiscoveryPort));
            var group = IPAddress.Parse(PairingCode.MulticastAddress);
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Append(IPAddress.Loopback).Distinct();
            foreach (var address in interfaces)
            {
                if (IPAddress.IsLoopback(listenAddress) && !IPAddress.IsLoopback(address)) continue;
                if (!listenAddress.Equals(IPAddress.Any) && !listenAddress.Equals(IPAddress.IPv6Any) && !listenAddress.Equals(address) && !IPAddress.IsLoopback(address)) continue;
                try { udp.JoinMulticastGroup(group, address); }
                catch (SocketException) { }
            }
            var expected = Encoding.UTF8.GetBytes(PairingCode.Protocol + PairingCode.Create(announcement));
            var response = Encoding.UTF8.GetBytes(Wire.Write(announcement));
            while (!stoppingToken.IsCancellationRequested)
            {
                var request = await udp.ReceiveAsync(stoppingToken);
                if (IPAddress.IsLoopback(listenAddress) && !IPAddress.IsLoopback(request.RemoteEndPoint.Address)) continue;
                if (!request.Buffer.AsSpan().SequenceEqual(expected)) continue;
                await udp.SendAsync(response, request.RemoteEndPoint, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e) when (e is SocketException or NetworkInformationException)
        {
            // A blocked discovery port must not terminate an active lesson or database.
            // The student receives an explicit discovery failure and can retry after network setup.
        }
    }
}
