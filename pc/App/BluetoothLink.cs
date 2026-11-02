using System.Diagnostics;
using ClipBridge.Core;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace ClipBridge;

/// <summary>
/// Reaches the tablet over Bluetooth, which works on networks that block devices
/// from talking to each other. Only phones and tablets already paired in Windows are
/// tried, because connecting to headphones or speakers can interrupt their audio.
/// </summary>
internal static class BluetoothLink
{
    /// <summary>Must match BLUETOOTH_SERVICE_ID in android/.../BridgeService.java.</summary>
    private static readonly Guid ServiceId = new("7c1b6f2e-3a54-4d8e-9f10-c11bb21d6e01");

    private const int ConnectTimeoutMs = 12_000;
    private const int HandshakeTimeoutMs = 10_000;

    private static BluetoothAddress? s_lastGood;

    public static SecureChannel? Connect(byte[] key, Action<string> log)
    {
        foreach (var device in Candidates())
        {
            var client = new BluetoothClient();
            try
            {
                var connect = Task.Run(() => client.Connect(device.DeviceAddress, ServiceId));
                if (!connect.Wait(ConnectTimeoutMs))
                {
                    log($"Bluetooth: {device.DeviceName} timed out");
                    client.Dispose();
                    continue;
                }
                var channel = SecureChannel.Handshake(client, client.GetStream(), key, HandshakeTimeoutMs, true, "Bluetooth");
                s_lastGood = device.DeviceAddress;
                log($"Bluetooth: connected through {device.DeviceName}");
                return channel;
            }
            catch (WrongCodeException)
            {
                client.Dispose();
                throw;
            }
            catch (Exception e)
            {
                log($"Bluetooth: {device.DeviceName}: {e.GetBaseException().Message}");
                client.Dispose();
            }
        }
        return null;
    }

    private static List<BluetoothDeviceInfo> Candidates()
    {
        using var lister = new BluetoothClient();
        return lister.PairedDevices
            .Where(d => d.ClassOfDevice.MajorDevice is DeviceClass.Phone or DeviceClass.Computer)
            .OrderByDescending(d => d.DeviceAddress.Equals(s_lastGood))
            .ToList();
    }

    /// <summary>
    /// Backs "ClipBridge.exe --test-bluetooth". Checks the real link and its speed using
    /// pings, which the tablet acknowledges without touching its clipboard.
    /// </summary>
    public static string Test(byte[] key, Action<string> log)
    {
        using var channel = Connect(key, log);
        if (channel == null) return "FAILED: no paired phone or tablet answered over Bluetooth";

        channel.SetTimeout(60_000);
        Ping(channel, 9);
        const int size = 200 * 1024;
        var timer = Stopwatch.StartNew();
        Ping(channel, size);
        double seconds = timer.Elapsed.TotalSeconds;
        return $"OK: {size / 1024} KB round trip in {seconds:0.0}s, about {size / 1024 / seconds:0} KB/s";
    }

    private static void Ping(SecureChannel channel, int size)
    {
        var frame = new byte[Math.Max(size, 9)];
        frame[0] = Proto.TypePing;
        long id = Random.Shared.NextInt64();
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1), id);
        channel.Send(frame);
        var reply = channel.Receive();
        if (reply.Length != 9 || reply[0] != Proto.TypeAck ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(1)) != id)
        {
            throw new IOException("unexpected reply from tablet");
        }
    }
}
