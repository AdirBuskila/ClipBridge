using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ClipBridge.Core;

/// <summary>Wire protocol. Must stay in step with android/src/com/clipbridge/Protocol.java.</summary>
public static class Proto
{
    public const int TcpPort = 47821;
    public const int UdpPort = 47822;

    public const byte TypeText = 1;
    public const byte TypePng = 2;
    public const byte TypePing = 3;
    public const byte TypeAck = 4;

    public const int MaxFrame = 128 * 1024 * 1024;

    /// <summary>
    /// Appended to every ping. Tells the tablet this PC reads clips the tablet sends, so
    /// a tablet never sends one to an older PC that would treat it as an error.
    /// </summary>
    public static readonly byte[] DuplexMarker = Encoding.ASCII.GetBytes("CB-DUPLEX");

    internal static readonly byte[] Hello = Encoding.ASCII.GetBytes("CB1\n");
    internal static readonly byte[] DiscoverMagic = Encoding.ASCII.GetBytes("CBD1");
    internal static readonly byte[] ReplyMagic = Encoding.ASCII.GetBytes("CBR1");

    public static string NormalizeCode(string code)
    {
        var sb = new StringBuilder();
        foreach (char c in code.ToUpperInvariant())
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') sb.Append(c);
        }
        return sb.ToString();
    }

    private const int LegacyCodeLength = 16;
    private const int KeyIterations = 300_000;

    /// <summary>True for the 8 characters the tablet displays, or an older 16-character code.</summary>
    public static bool IsValidCode(string code) => NormalizeCode(code).Length is 8 or LegacyCodeLength;

    /// <summary>
    /// Short codes go through a deliberately slow derivation, because anyone who can
    /// reach the tablet could otherwise test guesses against a handshake at full speed.
    /// Old 16-character codes keep their original derivation, so an existing pairing
    /// keeps working until the new code is entered.
    /// </summary>
    public static byte[] DeriveKey(string code)
    {
        var normalized = NormalizeCode(code);
        if (normalized.Length == LegacyCodeLength)
            return SHA256.HashData(Encoding.UTF8.GetBytes("ClipBridge-v1:" + normalized));
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalized),
            Encoding.ASCII.GetBytes("ClipBridge-v2-pairing"),
            KeyIterations,
            HashAlgorithmName.SHA256,
            32);
    }

    internal static byte[] Hmac(byte[] key, params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }

    internal static byte[] Label(string s) => Encoding.ASCII.GetBytes(s);
}

/// <summary>The tablet answered, but it does not hold the same pairing code.</summary>
public sealed class WrongCodeException : Exception
{
    public WrongCodeException() : base("pairing code does not match") { }
}

/// <summary>An authenticated, encrypted connection to the tablet.</summary>
public sealed class SecureChannel : IDisposable
{
    private readonly IDisposable _connection;
    private readonly Stream _stream;
    private readonly AesGcm _send;
    private readonly AesGcm _recv;
    private readonly object _sendLock = new();
    private ulong _sendCounter;
    private ulong _recvCounter;

    /// <summary>True on Bluetooth, which needs far longer delivery timeouts than Wi-Fi.</summary>
    public bool IsSlowLink { get; }

    /// <summary>What to show the user, such as an address or "Bluetooth".</summary>
    public string Description { get; }

    private SecureChannel(IDisposable connection, Stream stream, byte[] sendKey, byte[] recvKey, bool slow, string description)
    {
        _connection = connection;
        _stream = stream;
        _send = new AesGcm(sendKey, 16);
        _recv = new AesGcm(recvKey, 16);
        IsSlowLink = slow;
        Description = description;
    }

    public static SecureChannel Connect(string host, int port, byte[] key, int timeoutMs)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeoutMs))
                throw new TimeoutException("connect timed out");
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            return Handshake(tcp, tcp.GetStream(), key, timeoutMs, false, host);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Proves both sides hold the pairing code, over any connected stream.</summary>
    public static SecureChannel Handshake(IDisposable connection, Stream s, byte[] key, int timeoutMs, bool slow, string description)
    {
        ApplyTimeout(s, timeoutMs);

        var nc = RandomNumberGenerator.GetBytes(16);
        s.Write(Proto.Hello);
        s.Write(nc);
        s.Flush();

        var nt = new byte[16];
        s.ReadExactly(nt);
        var serverProof = new byte[32];
        s.ReadExactly(serverProof);
        if (!CryptographicOperations.FixedTimeEquals(serverProof, Proto.Hmac(key, Proto.Label("srv"), nc, nt)))
            throw new WrongCodeException();

        s.Write(Proto.Hmac(key, Proto.Label("cli"), nc, nt));
        s.Flush();

        var c2t = Proto.Hmac(key, Proto.Label("c2t"), nc, nt);
        var t2c = Proto.Hmac(key, Proto.Label("t2c"), nc, nt);
        return new SecureChannel(connection, s, c2t, t2c, slow, description);
    }

    private static void ApplyTimeout(Stream s, int ms)
    {
        if (!s.CanTimeout) return;
        s.ReadTimeout = ms;
        s.WriteTimeout = ms;
    }

    public void SetTimeout(int ms) => ApplyTimeout(_stream, ms);

    public void SetReadTimeout(int ms)
    {
        if (_stream.CanTimeout) _stream.ReadTimeout = ms;
    }

    public void SetWriteTimeout(int ms)
    {
        if (_stream.CanTimeout) _stream.WriteTimeout = ms;
    }

    private static byte[] Nonce(ulong counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    /// <summary>Safe to call from several threads. Only one thread may call Receive.</summary>
    public void Send(byte[] plain)
    {
        var buf = new byte[4 + plain.Length + 16];
        BinaryPrimitives.WriteInt32BigEndian(buf, plain.Length + 16);
        lock (_sendLock)
        {
            _send.Encrypt(Nonce(_sendCounter++), plain,
                buf.AsSpan(4, plain.Length), buf.AsSpan(4 + plain.Length, 16));
            _stream.Write(buf);
            _stream.Flush();
        }
    }

    public byte[] Receive()
    {
        var lenBuf = new byte[4];
        _stream.ReadExactly(lenBuf);
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < 16 || len > Proto.MaxFrame) throw new IOException($"bad frame length {len}");
        var ct = new byte[len];
        _stream.ReadExactly(ct);
        var plain = new byte[len - 16];
        _recv.Decrypt(Nonce(_recvCounter++), ct.AsSpan(0, len - 16), ct.AsSpan(len - 16, 16), plain);
        return plain;
    }

    public void Dispose()
    {
        _send.Dispose();
        _recv.Dispose();
        _connection.Dispose();
    }
}
