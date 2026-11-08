using System.Buffers.Binary;
using System.Net;

namespace ClipBridge.Core;

public enum LinkState
{
    NoCode,
    Paused,
    Searching,
    Connected,
    WrongCode,
}

/// <summary>
/// Keeps a connection to the tablet and delivers the latest clip. A clip stays
/// pending until the tablet confirms it landed on the clipboard, so a sleeping
/// tablet or a Wi-Fi drop delays delivery instead of losing it. Clips the tablet
/// sends come in on the same connection and raise ClipReceived.
/// </summary>
public sealed class TabletLink : IDisposable
{
    public int TcpPort { get; init; } = Proto.TcpPort;
    public int UdpPort { get; init; } = Proto.UdpPort;
    public int PingIntervalMs { get; init; } = 20_000;
    public int RetryDelayMs { get; init; } = 2_000;
    public int DiscoveryTimeoutMs { get; init; } = 1_500;

    /// <summary>Don't overwrite the tablet's clipboard with something copied long ago.</summary>
    public TimeSpan MaxClipAge { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Connects over Bluetooth, or returns null when the tablet isn't there.</summary>
    public Func<byte[], SecureChannel?>? BluetoothConnect { get; init; }

    public int BluetoothRetryMs { get; init; } = 15_000;

    /// <summary>Extra discovery targets, used by the tests.</summary>
    public IReadOnlyList<IPEndPoint>? ExtraDiscoveryTargets { get; init; }

    public event Action<LinkState, string?>? StateChanged;
    public event Action<long>? ClipDelivered;

    /// <summary>
    /// A clip from the tablet. Runs on the connection's reader thread and must return
    /// only once the clip is on the clipboard: the tablet is acknowledged afterwards, and
    /// throwing leaves it unacknowledged so the tablet sends it again.
    /// </summary>
    public event Action<byte, byte[]>? ClipReceived;
    public event Action<string>? Log;

    /// <summary>Called with the address whenever a connection succeeds, so it can be remembered.</summary>
    public event Action<string>? HostFound;

    private sealed record Clip(byte Type, long Id, byte[] Payload, DateTime Created);

    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private byte[]? _key;
    private int _generation;
    private string? _manualHost;
    private string? _lastHost;
    private bool _paused;
    private Clip? _pending;
    private long _lastId;
    private long _lastReceivedId = long.MinValue;
    private volatile bool _stop;
    private Thread? _thread;
    private LinkState _state = (LinkState)(-1);
    private long _nextBluetoothTry;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "tablet-link" };
        _thread.Start();
    }

    public void Configure(string? code, string? manualHost, string? lastHost)
    {
        lock (_lock)
        {
            _key = string.IsNullOrWhiteSpace(code) ? null : Proto.DeriveKey(code);
            _manualHost = string.IsNullOrWhiteSpace(manualHost) ? null : manualHost.Trim();
            _lastHost = lastHost;
            _generation++;
        }
        _wake.Set();
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            _paused = paused;
            if (paused) _pending = null;
            _generation++;
        }
        _wake.Set();
    }

    /// <summary>Queue a clip. A newer clip replaces one that has not been delivered yet.</summary>
    public long Send(byte type, byte[] payload)
    {
        long id;
        lock (_lock)
        {
            if (_paused) return 0;
            id = Math.Max(DateTime.UtcNow.Ticks, _lastId + 1);
            _lastId = id;
            _pending = new Clip(type, id, payload, DateTime.UtcNow);
        }
        _wake.Set();
        return id;
    }

    private void SetState(LinkState s, string? detail = null)
    {
        if (s == _state) return;
        _state = s;
        StateChanged?.Invoke(s, detail);
    }

    private void Run()
    {
        while (!_stop)
        {
            byte[]? key;
            bool paused;
            int generation;
            lock (_lock)
            {
                key = _key;
                paused = _paused;
                generation = _generation;
            }

            if (key == null || paused)
            {
                SetState(key == null ? LinkState.NoCode : LinkState.Paused);
                _wake.WaitOne(1000);
                continue;
            }

            if (_state != LinkState.WrongCode) SetState(LinkState.Searching);

            bool wrongCode = false;
            bool done = false;
            var (primary, secondary) = Candidates(key);

            // Wi-Fi the tablet answered on, then Bluetooth, then old addresses as a last resort.
            foreach (var host in primary)
            {
                if (done) break;
                done = Attempt(host, () => ConnectHost(host, key), generation, ref wrongCode);
            }
            if (!done && BluetoothConnect != null && Environment.TickCount64 >= _nextBluetoothTry)
            {
                done = Attempt("Bluetooth", () => BluetoothConnect(key), generation, ref wrongCode);
                // Bluetooth searches can make headphones stutter, so don't retry constantly.
                if (!done) _nextBluetoothTry = Environment.TickCount64 + BluetoothRetryMs;
            }
            foreach (var host in secondary)
            {
                if (done) break;
                done = Attempt(host, () => ConnectHost(host, key), generation, ref wrongCode);
            }

            if (_stop) break;
            if (generation != CurrentGeneration()) continue;
            SetState(wrongCode ? LinkState.WrongCode : LinkState.Searching);
            _wake.WaitOne(RetryDelayMs);
        }
    }

    private int CurrentGeneration()
    {
        lock (_lock) return _generation;
    }

    private SecureChannel ConnectHost(string host, byte[] key)
    {
        var ch = SecureChannel.Connect(host, TcpPort, key, 3000);
        lock (_lock) _lastHost = host;
        HostFound?.Invoke(host);
        return ch;
    }

    /// <summary>Runs one connection attempt and its whole session. True if it ever connected.</summary>
    private bool Attempt(string label, Func<SecureChannel?> connect, int generation, ref bool wrongCode)
    {
        if (_stop || generation != CurrentGeneration()) return true;
        try
        {
            using var ch = connect();
            if (ch == null) return false;
            wrongCode = false;
            SetState(LinkState.Connected, ch.Description);
            Log?.Invoke($"connected to {ch.Description}");
            Session(ch, generation);
            return true;
        }
        catch (WrongCodeException)
        {
            wrongCode = true;
            Log?.Invoke($"{label}: wrong pairing code");
            return false;
        }
        catch (Exception e)
        {
            Log?.Invoke($"{label}: {e.GetBaseException().Message}");
            // A connection that had been up has just dropped; start the search over.
            return _state == LinkState.Connected;
        }
    }

    private (List<string> Primary, List<string> Secondary) Candidates(byte[] key)
    {
        string? manual, last;
        lock (_lock)
        {
            manual = _manualHost;
            last = _lastHost;
        }

        // Discovery first: the tablet's address may have changed since last time.
        var primary = new List<string>();
        var found = Discovery.Find(key, UdpPort, DiscoveryTimeoutMs, ExtraDiscoveryTargets);
        if (found != null) primary.Add(found);

        // Some networks filter broadcasts but still pass direct traffic.
        var secondary = new List<string>();
        if (manual != null && !primary.Contains(manual)) secondary.Add(manual);
        if (last != null && !primary.Contains(last) && !secondary.Contains(last)) secondary.Add(last);
        return (primary, secondary);
    }

    private void Session(SecureChannel ch, int generation)
    {
        // The reader always has something to read within a ping interval (the ping's ack),
        // so a long silence means the link is dead even if the main loop is idle.
        ch.SetReadTimeout(PingIntervalMs * 2 + (ch.IsSlowLink ? 60_000 : 30_000));
        var acks = new AckBox();
        var reader = new Thread(() => ReadLoop(ch, acks)) { IsBackground = true, Name = "tablet-reader" };
        reader.Start();

        // Ping straight away so the tablet learns at once that it may send clips.
        Ping(ch, acks);
        long nextPing = Environment.TickCount64 + PingIntervalMs;
        while (!_stop && generation == CurrentGeneration())
        {
            acks.ThrowIfFailed();
            var clip = TakePending();
            if (clip != null)
            {
                var frame = new byte[9 + clip.Payload.Length];
                frame[0] = clip.Type;
                BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1), clip.Id);
                clip.Payload.CopyTo(frame, 9);

                // Allow for slow Wi-Fi (8s plus 256 KB/s) or Bluetooth (20s plus 15 KB/s,
                // about half the speed measured on a real link).
                int timeout = ch.IsSlowLink
                    ? 20_000 + clip.Payload.Length / 15
                    : 8000 + clip.Payload.Length / 256;
                ch.SetWriteTimeout(timeout);
                ch.Send(frame);
                acks.Wait(clip.Id, timeout);
                ClearPending(clip);
                Log?.Invoke($"delivered clip {clip.Id} ({clip.Payload.Length} bytes)");
                ClipDelivered?.Invoke(clip.Id);
                nextPing = Environment.TickCount64 + PingIntervalMs;
                continue;
            }

            long now = Environment.TickCount64;
            if (now >= nextPing)
            {
                Ping(ch, acks);
                nextPing = Environment.TickCount64 + PingIntervalMs;
                continue;
            }

            _wake.WaitOne((int)Math.Min(nextPing - now, 1000));
        }
    }

    private static void Ping(SecureChannel ch, AckBox acks)
    {
        long pingId = Environment.TickCount64;
        var frame = new byte[9 + Proto.DuplexMarker.Length];
        frame[0] = Proto.TypePing;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1), pingId);
        Proto.DuplexMarker.CopyTo(frame, 9);
        int timeout = ch.IsSlowLink ? 20_000 : 10_000;
        ch.SetWriteTimeout(timeout);
        ch.Send(frame);
        acks.Wait(pingId, timeout);
    }

    /// <summary>Reads everything the tablet sends: acks for our frames, and its own clips.</summary>
    private void ReadLoop(SecureChannel ch, AckBox acks)
    {
        try
        {
            while (true)
            {
                var frame = ch.Receive();
                if (frame.Length < 9) throw new IOException("short frame from tablet");
                byte type = frame[0];
                long id = BinaryPrimitives.ReadInt64BigEndian(frame.AsSpan(1));
                if (type == Proto.TypeAck)
                {
                    acks.Ack(id);
                }
                else if (type is Proto.TypeText or Proto.TypePng)
                {
                    // A retry of a clip that already landed (its ack got lost) is only re-acked.
                    if (id != _lastReceivedId)
                    {
                        var payload = frame.AsSpan(9).ToArray();
                        try
                        {
                            ClipReceived?.Invoke(type, payload);
                        }
                        catch (Exception e)
                        {
                            throw new IOException("could not apply clip from tablet: " + e.GetBaseException().Message, e);
                        }
                        _lastReceivedId = id;
                        Log?.Invoke($"received clip {id} from tablet ({payload.Length} bytes)");
                    }
                    var ack = new byte[9];
                    ack[0] = Proto.TypeAck;
                    BinaryPrimitives.WriteInt64BigEndian(ack.AsSpan(1), id);
                    ch.Send(ack);
                }
                else
                {
                    throw new IOException($"unknown frame type {type} from tablet");
                }
            }
        }
        catch (Exception e)
        {
            acks.Fail(e);
            // Wake the main loop so it notices at once. After Dispose there is nothing to wake.
            try
            {
                if (!_stop) _wake.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Hands acks from the reader thread to the thread waiting for them.</summary>
    private sealed class AckBox
    {
        private readonly object _gate = new();
        private long _last = long.MinValue;
        private Exception? _error;

        public void Ack(long id)
        {
            lock (_gate)
            {
                _last = id;
                Monitor.PulseAll(_gate);
            }
        }

        public void Fail(Exception e)
        {
            lock (_gate)
            {
                _error ??= e;
                Monitor.PulseAll(_gate);
            }
        }

        public void ThrowIfFailed()
        {
            lock (_gate)
            {
                if (_error != null) throw new IOException(_error.GetBaseException().Message, _error);
            }
        }

        /// <summary>Frames are acked in order and only one waits at a time, so the last ack is enough.</summary>
        public void Wait(long id, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            lock (_gate)
            {
                while (_last != id)
                {
                    if (_error != null) throw new IOException(_error.GetBaseException().Message, _error);
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0) throw new TimeoutException("tablet did not acknowledge in time");
                    Monitor.Wait(_gate, (int)Math.Min(left, int.MaxValue));
                }
            }
        }
    }

    private Clip? TakePending()
    {
        lock (_lock)
        {
            if (_pending != null && DateTime.UtcNow - _pending.Created > MaxClipAge)
            {
                Log?.Invoke($"dropped clip {_pending.Id}: older than {MaxClipAge.TotalMinutes} minutes");
                _pending = null;
            }
            return _pending;
        }
    }

    private void ClearPending(Clip clip)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_pending, clip)) _pending = null;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _wake.Dispose();
    }
}
