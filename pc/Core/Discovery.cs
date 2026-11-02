using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ClipBridge.Core;

/// <summary>
/// Finds the tablet by broadcasting on every network this PC is on, including the
/// Windows mobile hotspot. Replies are proven with the pairing code, so another
/// ClipBridge on a shared network is ignored.
/// </summary>
public static class Discovery
{
    public static string? Find(byte[] key, int udpPort, int timeoutMs, IEnumerable<IPEndPoint>? extraTargets = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var request = Proto.DiscoverMagic
            .Concat(nonce)
            .Concat(Proto.Hmac(key, Proto.Label("req"), nonce))
            .ToArray();
        var expected = Proto.ReplyMagic
            .Concat(Proto.Hmac(key, Proto.Label("rep"), nonce))
            .ToArray();

        var sockets = new List<(Socket Socket, List<IPEndPoint> Targets)>();
        try
        {
            foreach (var (local, targets) in LocalNetworks(udpPort))
            {
                try
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                    {
                        EnableBroadcast = true,
                    };
                    s.Bind(new IPEndPoint(local, 0));
                    sockets.Add((s, targets));
                }
                catch (SocketException)
                {
                    // That network just went away; the others can still answer.
                }
            }

            if (extraTargets != null)
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(IPAddress.Any, 0));
                sockets.Add((s, extraTargets.ToList()));
            }

            if (sockets.Count == 0) return null;

            var deadline = Environment.TickCount64 + timeoutMs;
            var buf = new byte[128];
            long nextSend = 0;
            while (true)
            {
                long now = Environment.TickCount64;
                if (now >= deadline) return null;

                // Wi-Fi drops broadcasts freely, so send a few times.
                if (now >= nextSend)
                {
                    foreach (var (s, targets) in sockets)
                    {
                        foreach (var t in targets)
                        {
                            try { s.SendTo(request, t); } catch (SocketException) { }
                        }
                    }
                    nextSend = now + 400;
                }

                var ready = sockets.Select(x => x.Socket).ToList();
                Socket.Select(ready, null, null, 100_000);
                foreach (var s in ready)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref from); }
                    catch (SocketException) { continue; }
                    if (n == expected.Length &&
                        CryptographicOperations.FixedTimeEquals(buf.AsSpan(0, n), expected))
                    {
                        return ((IPEndPoint)from).Address.ToString();
                    }
                }
            }
        }
        finally
        {
            foreach (var (s, _) in sockets) s.Dispose();
        }
    }

    private static IEnumerable<(IPAddress Local, List<IPEndPoint> Targets)> LocalNetworks(int udpPort)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = addr.Address.GetAddressBytes();
                var mask = addr.IPv4Mask.GetAddressBytes();
                var bcast = new byte[4];
                for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return (addr.Address, new List<IPEndPoint>
                {
                    new(new IPAddress(bcast), udpPort),
                    new(IPAddress.Broadcast, udpPort),
                });
            }
        }
    }
}
