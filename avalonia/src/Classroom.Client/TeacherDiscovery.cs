using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Classroom.Contracts;

namespace Classroom.Client;

public static class TeacherDiscovery
{
    public static async Task<ConnectionSettings> Find(string input, CancellationToken cancellation = default)
    {
        try { return await FindCore(input, cancellation); }
        catch (Exception e) when (e is SocketException or NetworkInformationException)
        {
            throw new IOException("Не удалось найти преподавателя в локальной сети. Проверьте подключение к сети.", e);
        }
    }

    static async Task<ConnectionSettings> FindCore(string input, CancellationToken cancellation)
    {
        var code = PairingCode.Normalize(input);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        udp.MulticastLoopback = true;
        udp.Ttl = 1;
        var request = Encoding.UTF8.GetBytes(PairingCode.Protocol + code);
        var endpoint = new IPEndPoint(IPAddress.Parse(PairingCode.MulticastAddress), PairingCode.DiscoveryPort);
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Append(IPAddress.Loopback).Distinct().ToArray();
        async Task Send()
        {
            foreach (var address in addresses)
            {
                try
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    await udp.SendAsync(request, endpoint, timeout.Token);
                }
                catch (SocketException) { /* One unavailable adapter must not hide other adapters. */ }
            }
        }
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Send();
                using var round = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                round.CancelAfter(TimeSpan.FromSeconds(2));
                while (!round.IsCancellationRequested)
                {
                    UdpReceiveResult reply;
                    try { reply = await udp.ReceiveAsync(round.Token); }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { break; }
                    if (reply.Buffer.Length > 1024) continue;
                    try
                    {
                        var announcement = Wire.Read<PairingAnnouncement>(Encoding.UTF8.GetString(reply.Buffer));
                        if (PairingCode.Create(announcement) != code) continue;
                        return new($"https://{reply.RemoteEndPoint.Address}:{announcement.Port}", announcement.Fingerprint);
                    }
                    catch (Exception e) when (e is JsonException or InvalidDataException) { }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        cancellation.ThrowIfCancellationRequested();
        throw new IOException("Преподаватель с таким кодом не найден. Проверьте код и подключение к одной сети. После перезапуска преподавателя код меняется.");
    }
}
