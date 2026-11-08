using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ClipBridge.Core;

// Runs the real PC networking code against the tablet's real networking code
// (android/src/.../BridgeServer.java on a desktop JVM). Usage: ClipBridgeTests <javaClassesDir>

const string Code = "ABCD-EFGH";
const int Tcp = 47921;
const int Udp = 47922;
string classes = args[0];
int failed = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail == "" ? "" : "  -- " + detail)}");
    if (!ok) failed++;
}

TabletLink NewLink() => new()
{
    TcpPort = Tcp,
    UdpPort = Udp,
    RetryDelayMs = 200,
    DiscoveryTimeoutMs = 400,
    PingIntervalMs = 500,
    ExtraDiscoveryTargets = [new IPEndPoint(IPAddress.Loopback, Udp)],
};

static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

static bool WaitFor(Func<bool> cond, int ms)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms)
    {
        if (cond()) return true;
        Thread.Sleep(20);
    }
    return cond();
}

using (var tablet = new FakeTablet(classes, Code, Tcp, Udp))
{
    // --- Normal delivery, found through discovery alone
    var states = new ConcurrentQueue<LinkState>();
    var delivered = new ConcurrentQueue<long>();
    using (var link = NewLink())
    {
        link.StateChanged += (s, _) => states.Enqueue(s);
        link.ClipDelivered += delivered.Enqueue;
        link.Configure(Code, null, null);
        link.Start();
        Check("finds tablet by discovery and connects", WaitFor(() => states.Contains(LinkState.Connected), 5000));

        var text = Encoding.UTF8.GetBytes("hello שלום 🎉");
        long id = link.Send(Proto.TypeText, text);
        Check("text clip acked", WaitFor(() => delivered.Contains(id), 3000));
        Check("text arrives byte-for-byte", tablet.HasClip(Proto.TypeText, id, text));

        var big = RandomNumberGenerator.GetBytes(20 * 1024 * 1024);
        long bigId = link.Send(Proto.TypePng, big);
        Check("20 MB image acked", WaitFor(() => delivered.Contains(bigId), 15000));
        Check("20 MB image arrives intact", tablet.HasClip(Proto.TypePng, bigId, big));

        var before = tablet.ClipCount;
        Thread.Sleep(1600);
        Check("stays connected across pings", tablet.ClipCount == before && !tablet.Lines.Contains("DISCONNECTED"));

        // --- Tablet goes away and comes back (sleep, Wi-Fi drop, reboot)
        tablet.Kill();
        Check("notices the tablet is gone", WaitFor(() => states.Last() == LinkState.Searching, 5000));

        var stale = Encoding.UTF8.GetBytes("superseded");
        var latest = Encoding.UTF8.GetBytes("latest copy");
        long staleId = link.Send(Proto.TypeText, stale);
        long latestId = link.Send(Proto.TypeText, latest);
        Thread.Sleep(500);

        tablet.Start();
        Check("reconnects by itself", WaitFor(() => delivered.Contains(latestId), 8000));
        Check("clip copied while offline arrives", tablet.HasClip(Proto.TypeText, latestId, latest));
        Check("only the newest offline clip is sent", !tablet.HasClip(Proto.TypeText, staleId, stale));
    }

    // --- Old clips are dropped rather than overwriting the clipboard
    {
        tablet.Kill();
        var del = new ConcurrentQueue<long>();
        using var link = new TabletLink
        {
            TcpPort = Tcp,
            UdpPort = Udp,
            RetryDelayMs = 200,
            DiscoveryTimeoutMs = 300,
            MaxClipAge = TimeSpan.FromMilliseconds(300),
            ExtraDiscoveryTargets = [new IPEndPoint(IPAddress.Loopback, Udp)],
        };
        link.ClipDelivered += del.Enqueue;
        link.Configure(Code, null, null);
        link.Start();
        var old = Encoding.UTF8.GetBytes("too old");
        long oldId = link.Send(Proto.TypeText, old);
        Thread.Sleep(600);
        tablet.Start();
        Thread.Sleep(2000);
        Check("clip older than the limit is dropped", !tablet.HasClip(Proto.TypeText, oldId, old));
    }

    // --- Wrong code
    {
        var st = new ConcurrentQueue<LinkState>();
        using var link = new TabletLink { TcpPort = Tcp, UdpPort = Udp, RetryDelayMs = 200, DiscoveryTimeoutMs = 300 };
        link.StateChanged += (s, _) => st.Enqueue(s);
        link.Configure("WRNG-WRNG-WRNG-WRNG", "127.0.0.1", null);
        link.Start();
        Check("wrong code is reported", WaitFor(() => st.Contains(LinkState.WrongCode), 5000));
        Check("wrong code never connects", !st.Contains(LinkState.Connected));
        var secret = Encoding.UTF8.GetBytes("must not arrive");
        long id = link.Send(Proto.TypeText, secret);
        Thread.Sleep(700);
        Check("wrong code delivers nothing", !tablet.HasClip(Proto.TypeText, id, secret));
    }

    // --- Discovery stays silent for the wrong code
    Check("discovery ignores wrong code",
        Discovery.Find(Proto.DeriveKey("WRNG-WRNG-WRNG-WRNG"), Udp, 500, [new IPEndPoint(IPAddress.Loopback, Udp)]) == null);
    Check("discovery answers right code",
        Discovery.Find(Proto.DeriveKey(Code), Udp, 1000, [new IPEndPoint(IPAddress.Loopback, Udp)]) != null);

    // --- A tampered frame must not reach the clipboard
    {
        int beforeCount = tablet.ClipCount;
        using var tcp = new TcpClient("127.0.0.1", Tcp);
        var s = tcp.GetStream();
        var key = Proto.DeriveKey(Code);
        var nc = RandomNumberGenerator.GetBytes(16);
        s.Write(Encoding.ASCII.GetBytes("CB1\n"));
        s.Write(nc);
        var nt = new byte[16];
        s.ReadExactly(nt);
        s.ReadExactly(new byte[32]);
        using (var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key))
        {
            h.AppendData(Encoding.ASCII.GetBytes("cli"));
            h.AppendData(nc);
            h.AppendData(nt);
            s.Write(h.GetHashAndReset());
        }
        var junk = RandomNumberGenerator.GetBytes(64);
        s.Write([0, 0, 0, 64]);
        s.Write(junk);
        s.Flush();
        tcp.ReceiveTimeout = 3000;
        bool closed;
        try { closed = s.Read(new byte[16]) == 0; }
        catch (IOException) { closed = true; }
        Check("forged frame closes the connection", closed);
        Check("forged frame never reaches the clipboard", tablet.ClipCount == beforeCount);
    }

    // --- A stranger without the code cannot get in
    {
        using var tcp = new TcpClient("127.0.0.1", Tcp);
        var s = tcp.GetStream();
        s.Write(Encoding.ASCII.GetBytes("CB1\n"));
        s.Write(RandomNumberGenerator.GetBytes(16));
        var reply = new byte[48];
        s.ReadExactly(reply);
        s.Write(RandomNumberGenerator.GetBytes(32));
        s.Flush();
        tcp.ReceiveTimeout = 3000;
        bool closed;
        try { closed = s.Read(new byte[16]) == 0; }
        catch (IOException) { closed = true; }
        Check("bad handshake is rejected", closed);
    }

    // --- Spaces, dashes and lowercase in a typed code
    Check("code formatting doesn't matter",
        Proto.DeriveKey("abcd efgh").SequenceEqual(Proto.DeriveKey(Code)));
}

// --- Tablet to PC
using (var tablet = new FakeTablet(classes, Code, Tcp, Udp))
{
    // An older PC never marks its pings, so it must never be sent a clip it can't read.
    var early = Encoding.UTF8.GetBytes("copied on the tablet before a new PC connected");
    tablet.SendToPc("out-0.txt", early);
    Check("tablet queues a clip for the PC", WaitFor(() => tablet.Queued("out-0.txt") != null, 3000));
    using (var old = SecureChannel.Connect("127.0.0.1", Tcp, Proto.DeriveKey(Code), 3000))
    {
        bool onlyAcks = true;
        for (int i = 0; i < 3; i++)
        {
            long pingId = 1000 + i;
            var ping = new byte[9];
            ping[0] = Proto.TypePing;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(1), pingId);
            old.Send(ping);
            var reply = old.Receive();
            onlyAcks &= reply.Length == 9 && reply[0] == Proto.TypeAck &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(1)) == pingId;
            Thread.Sleep(300);
        }
        Check("an older PC is never sent a tablet clip", onlyAcks);
    }

    var received = new ConcurrentQueue<(byte Type, string Sha)>();
    var states = new ConcurrentQueue<LinkState>();
    var delivered = new ConcurrentQueue<long>();
    var link = NewLink();
    try
    {
        link.ClipReceived += (t, p) => received.Enqueue((t, Sha(p)));
        link.StateChanged += (st, _) => states.Enqueue(st);
        link.ClipDelivered += delivered.Enqueue;
        link.Configure(Code, null, null);
        link.Start();
        Check("clip queued before the PC connected arrives", WaitFor(() => received.Contains((Proto.TypeText, Sha(early))), 5000));
        Check("tablet is told the PC got it", WaitFor(() => tablet.Sent("out-0.txt"), 3000));

        var text = Encoding.UTF8.GetBytes("from the tablet שלום 🎉");
        tablet.SendToPc("out-1.txt", text);
        Check("tablet text reaches the PC byte-for-byte", WaitFor(() => received.Contains((Proto.TypeText, Sha(text))), 3000));

        var img = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        tablet.SendToPc("out-2.png", img);
        Check("5 MB tablet image reaches the PC intact", WaitFor(() => received.Contains((Proto.TypePng, Sha(img))), 10000));

        // Large clips crossing in both directions at once must not jam the connection.
        var toTablet = RandomNumberGenerator.GetBytes(20 * 1024 * 1024);
        var toPc = RandomNumberGenerator.GetBytes(10 * 1024 * 1024);
        long toTabletId = link.Send(Proto.TypePng, toTablet);
        tablet.SendToPc("out-3.png", toPc);
        Check("PC to tablet while tablet sends to PC", WaitFor(() => delivered.Contains(toTabletId), 20000) && tablet.HasClip(Proto.TypePng, toTabletId, toTablet));
        Check("tablet to PC while PC sends to tablet", WaitFor(() => received.Contains((Proto.TypePng, Sha(toPc))), 20000));

        int count = received.Count;
        int drops = tablet.Lines.Count(l => l == "DISCONNECTED");
        Thread.Sleep(1600);
        Check("each tablet clip arrives once, connection stays up",
            received.Count == count && count == 4 && tablet.Lines.Count(l => l == "DISCONNECTED") == drops && states.Last() == LinkState.Connected,
            $"received={received.Count} drops={drops}->{tablet.Lines.Count(l => l == "DISCONNECTED")} state={states.Last()}");
    }
    finally
    {
        link.Dispose();
    }

    // Copied on the tablet while the PC is away: delivered when the PC comes back.
    var offline = Encoding.UTF8.GetBytes("copied while the PC was away");
    tablet.SendToPc("out-4.txt", offline);
    WaitFor(() => tablet.Queued("out-4.txt") != null, 3000);
    Thread.Sleep(500);
    var later = new ConcurrentQueue<string>();
    using (var link2 = NewLink())
    {
        link2.ClipReceived += (_, p) => later.Enqueue(Sha(p));
        link2.Configure(Code, null, null);
        link2.Start();
        Check("tablet clip waits for the PC to come back", WaitFor(() => later.Contains(Sha(offline)), 6000));
    }
}

// --- An existing 16-character pairing keeps working after the update
using (var oldTablet = new FakeTablet(classes, "ABCD-EFGH-JKMN-PQRS", Tcp, Udp))
{
    var states = new ConcurrentQueue<LinkState>();
    var delivered = new ConcurrentQueue<long>();
    using var link = NewLink();
    link.StateChanged += (s, _) => states.Enqueue(s);
    link.ClipDelivered += delivered.Enqueue;
    link.Configure("ABCD-EFGH-JKMN-PQRS", null, null);
    link.Start();
    Check("old 16-character code still connects", WaitFor(() => states.Contains(LinkState.Connected), 5000));
    var text = Encoding.UTF8.GetBytes("old pairing");
    long id = link.Send(Proto.TypeText, text);
    Check("old 16-character code still delivers", WaitFor(() => delivered.Contains(id), 3000) && oldTablet.HasClip(Proto.TypeText, id, text));
}

// --- Bluetooth fallback: Wi-Fi unavailable, the Bluetooth path carries the clip
using (var tablet = new FakeTablet(classes, Code, Tcp, Udp))
{
    var states = new ConcurrentQueue<LinkState>();
    var delivered = new ConcurrentQueue<long>();
    int calls = 0;
    using var link = new TabletLink
    {
        TcpPort = Tcp + 100, // Nothing listens here, so Wi-Fi can't work.
        UdpPort = Udp + 100,
        RetryDelayMs = 200,
        DiscoveryTimeoutMs = 300,
        BluetoothConnect = key =>
        {
            Interlocked.Increment(ref calls);
            var tcp = new TcpClient("127.0.0.1", Tcp); // Stands in for the Bluetooth stream.
            return SecureChannel.Handshake(tcp, tcp.GetStream(), key, 5000, true, "Bluetooth");
        },
    };
    string? via = null;
    link.StateChanged += (s, d) => { states.Enqueue(s); if (s == LinkState.Connected) via = d; };
    link.ClipDelivered += delivered.Enqueue;
    link.Configure(Code, null, null);
    link.Start();
    Check("falls back to Bluetooth when Wi-Fi fails", WaitFor(() => states.Contains(LinkState.Connected), 5000) && via == "Bluetooth");
    var text = Encoding.UTF8.GetBytes("over bluetooth path");
    long id = link.Send(Proto.TypeText, text);
    Check("clip delivered through the fallback", WaitFor(() => delivered.Contains(id), 3000) && tablet.HasClip(Proto.TypeText, id, text));
}

Console.WriteLine(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
return failed == 0 ? 0 : 1;

sealed class FakeTablet : IDisposable
{
    private readonly string _classes, _code;
    private readonly int _tcp, _udp;
    private Process? _p;
    private readonly string _outbox = Path.Combine(Path.GetTempPath(), "cb-outbox-" + Guid.NewGuid().ToString("N"));
    public ConcurrentQueue<string> Lines { get; } = new();

    /// <summary>Makes the tablet send a clip to the PC, as if copied there. Name it out-*.txt or out-*.png.</summary>
    public void SendToPc(string name, byte[] payload)
    {
        var tmp = Path.Combine(_outbox, name + ".tmp");
        File.WriteAllBytes(tmp, payload);
        File.Move(tmp, Path.Combine(_outbox, name));
    }

    /// <summary>The id the tablet gave a clip from SendToPc, once queued.</summary>
    public string? Queued(string name) =>
        Lines.FirstOrDefault(l => l.StartsWith($"QUEUED {name} "))?.Split(' ')[2];

    /// <summary>True once the PC confirmed the clip landed on its clipboard.</summary>
    public bool Sent(string name)
    {
        var id = Queued(name);
        return id != null && Lines.Contains("SENT " + id);
    }

    public FakeTablet(string classes, string code, int tcp, int udp)
    {
        (_classes, _code, _tcp, _udp) = (classes, code, tcp, udp);
        Start();
    }

    public int ClipCount => Lines.Count(l => l.StartsWith("CLIP "));

    public bool HasClip(byte type, long id, byte[] payload)
    {
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return Lines.Contains($"CLIP {type} {id} {payload.Length} {sha}");
    }

    public void Start()
    {
        var psi = new ProcessStartInfo("java")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        Directory.CreateDirectory(_outbox);
        psi.Environment["CB_OUTBOX_DIR"] = _outbox;
        foreach (var a in new[] { "-cp", _classes, "InteropServer", _code, _tcp.ToString(), _udp.ToString() })
            psi.ArgumentList.Add(a);
        var ready = new ManualResetEventSlim();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Lines.Enqueue(e.Data);
            if (e.Data == "READY") ready.Set();
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Lines.Enqueue("ERR " + e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _p = p;
        if (!ready.Wait(15000)) throw new Exception("java server did not start:\n" + string.Join("\n", Lines));
    }

    public void Kill()
    {
        if (_p == null) return;
        try { _p.Kill(true); _p.WaitForExit(5000); } catch { }
        _p = null;
    }

    public void Dispose()
    {
        Kill();
        try { Directory.Delete(_outbox, true); } catch { }
    }
}
