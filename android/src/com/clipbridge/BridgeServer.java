package com.clipbridge;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.Closeable;
import java.io.DataInputStream;
import java.io.FilterInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Arrays;

/**
 * Listens for the PC. The tablet is the listener because Android has no inbound
 * firewall, while Windows blocks inbound traffic on "Public" networks. Once connected,
 * clips go both ways over the same connection: the tablet sends its own only after the
 * PC's pings show it can read them, so an older PC is never sent one.
 *
 * No Android imports: the interop tests run this class on a desktop JVM. Bluetooth
 * connections are accepted by BridgeService and handed to handleConnection.
 */
public final class BridgeServer {
    public interface Listener {
        /** Apply a clip. Throwing means no ack is sent, so the PC retries. */
        void onClip(byte type, long id, byte[] payload) throws Exception;

        void onConnectionChanged(boolean connected, String peer);

        /** The PC confirmed a clip from sendToPc landed on its clipboard. */
        void onSentToPc(long id);

        void onLog(String message);
    }

    private static final int HANDSHAKE_TIMEOUT_MS = 10000;
    /** PC pings every 20s; a little over two missed pings means the link is dead. */
    private static final int IDLE_TIMEOUT_MS = 50000;
    /** Same limit the PC uses: don't overwrite its clipboard with something copied long ago. */
    private static final long MAX_CLIP_AGE_MS = 10 * 60 * 1000;

    private final byte[] key;
    private final int tcpPort;
    private final int udpPort;
    private final Listener listener;
    private final SecureRandom random = new SecureRandom();

    private volatile boolean running;
    private volatile ServerSocket serverSocket;
    private volatile DatagramSocket udpSocket;
    private Closeable activeConnection;
    private final Object activeLock = new Object();
    private long lastClipId = Long.MIN_VALUE;

    /** The newest clip for the PC, kept until the PC confirms it. */
    private Outgoing outgoing;
    private long lastOutId;
    private final Object outLock = new Object();

    private static final class Outgoing {
        final byte type;
        final long id;
        final byte[] payload;
        final long created = System.currentTimeMillis();

        Outgoing(byte type, long id, byte[] payload) {
            this.type = type;
            this.id = id;
            this.payload = payload;
        }
    }

    public BridgeServer(byte[] key, int tcpPort, int udpPort, Listener listener) {
        this.key = key;
        this.tcpPort = tcpPort;
        this.udpPort = udpPort;
        this.listener = listener;
    }

    public void start() {
        running = true;
        new Thread(this::acceptLoop, "cb-accept").start();
        new Thread(this::discoveryLoop, "cb-discovery").start();
    }

    /**
     * Queues a clip for the PC. A newer clip replaces one not yet delivered. It goes out
     * as soon as a PC that reads clips is connected, or when one next connects.
     */
    public long sendToPc(byte type, byte[] payload) {
        synchronized (outLock) {
            long id = Math.max(System.currentTimeMillis(), lastOutId + 1);
            lastOutId = id;
            outgoing = new Outgoing(type, id, payload);
            outLock.notifyAll();
            return id;
        }
    }

    /** True while a clip for the PC is waiting. */
    public boolean hasPendingForPc() {
        synchronized (outLock) {
            return outgoing != null;
        }
    }

    public void stop() {
        running = false;
        closeQuietly(serverSocket);
        DatagramSocket udp = udpSocket;
        if (udp != null) udp.close();
        synchronized (activeLock) {
            closeQuietly(activeConnection);
        }
    }

    private void acceptLoop() {
        while (running) {
            try (ServerSocket ss = new ServerSocket()) {
                ss.setReuseAddress(true);
                ss.bind(new InetSocketAddress(tcpPort));
                serverSocket = ss;
                listener.onLog("listening on TCP " + tcpPort);
                while (running) {
                    Socket s = ss.accept();
                    new Thread(() -> handleTcp(s), "cb-conn").start();
                }
            } catch (IOException e) {
                if (running) {
                    listener.onLog("accept failed: " + e);
                    sleep(2000);
                }
            }
        }
    }

    private void discoveryLoop() {
        byte[] buf = new byte[128];
        while (running) {
            try (DatagramSocket udp = new DatagramSocket(null)) {
                udp.setReuseAddress(true);
                udp.setBroadcast(true);
                udp.bind(new InetSocketAddress(udpPort));
                udpSocket = udp;
                while (running) {
                    DatagramPacket p = new DatagramPacket(buf, buf.length);
                    udp.receive(p);
                    byte[] reply = discoveryReply(Arrays.copyOfRange(p.getData(), p.getOffset(), p.getOffset() + p.getLength()));
                    if (reply != null) {
                        udp.send(new DatagramPacket(reply, reply.length, p.getSocketAddress()));
                    }
                }
            } catch (IOException e) {
                if (running) {
                    listener.onLog("discovery failed: " + e);
                    sleep(2000);
                }
            }
        }
    }

    /** Request: magic + nonce(16) + HMAC. Only someone holding the code gets an answer. */
    private byte[] discoveryReply(byte[] m) {
        if (m.length != 4 + 16 + 32) return null;
        if (!Arrays.equals(Arrays.copyOfRange(m, 0, 4), Protocol.DISCOVER_MAGIC)) return null;
        byte[] nonce = Arrays.copyOfRange(m, 4, 20);
        byte[] tag = Arrays.copyOfRange(m, 20, 52);
        if (!MessageDigest.isEqual(tag, Protocol.hmac(key, Protocol.ascii("req"), nonce))) return null;
        byte[] rep = Protocol.hmac(key, Protocol.ascii("rep"), nonce);
        return ByteBuffer.allocate(4 + 32).put(Protocol.REPLY_MAGIC).put(rep).array();
    }

    private void handleTcp(Socket s) {
        try {
            s.setTcpNoDelay(true);
            s.setKeepAlive(true);
            handleConnection(s.getInputStream(), s.getOutputStream(), s, String.valueOf(s.getRemoteSocketAddress()));
        } catch (IOException e) {
            listener.onLog("connection failed: " + e.getMessage());
            closeQuietly(s);
        }
    }

    /**
     * Serves one connection until it ends, whatever carries it. Bluetooth streams have
     * no read timeouts, so a watchdog closes any connection that goes quiet.
     */
    public void handleConnection(InputStream in, OutputStream out, Closeable connection, String peer) {
        Watchdog watchdog = new Watchdog(connection);
        boolean authenticated = false;
        Session session = null;
        try {
            watchdog.start();
            InputStream rawIn = new BufferedInputStream(new WatchedInput(in, watchdog));
            OutputStream rawOut = new BufferedOutputStream(out);
            Protocol.Channel ch = handshake(rawIn, rawOut);
            if (ch == null) {
                listener.onLog("rejected connection from " + peer);
                return;
            }
            authenticated = true;
            synchronized (activeLock) {
                // The newest authenticated PC wins; an old half-dead link is dropped.
                closeQuietly(activeConnection);
                activeConnection = connection;
            }
            watchdog.authenticated();
            listener.onConnectionChanged(true, peer);
            session = new Session(this, ch, connection, "Bluetooth".equals(peer));
            serve(session);
        } catch (IOException e) {
            listener.onLog("connection ended (" + peer + "): " + e.getMessage());
        } finally {
            if (session != null) session.end();
            watchdog.finish();
            closeQuietly(connection);
            if (authenticated) {
                boolean wasActive;
                synchronized (activeLock) {
                    wasActive = activeConnection == connection;
                    if (wasActive) activeConnection = null;
                }
                if (wasActive) listener.onConnectionChanged(false, peer);
            }
        }
    }

    private Protocol.Channel handshake(InputStream rawIn, OutputStream rawOut) throws IOException {
        DataInputStream in = new DataInputStream(rawIn);
        byte[] hello = new byte[4];
        in.readFully(hello);
        if (!Arrays.equals(hello, Protocol.HELLO)) return null;
        byte[] nc = new byte[16];
        in.readFully(nc);
        byte[] nt = new byte[16];
        random.nextBytes(nt);
        rawOut.write(nt);
        rawOut.write(Protocol.hmac(key, Protocol.ascii("srv"), nc, nt));
        rawOut.flush();
        byte[] clientProof = new byte[32];
        in.readFully(clientProof);
        if (!MessageDigest.isEqual(clientProof, Protocol.hmac(key, Protocol.ascii("cli"), nc, nt))) return null;
        byte[] c2t = Protocol.hmac(key, Protocol.ascii("c2t"), nc, nt);
        byte[] t2c = Protocol.hmac(key, Protocol.ascii("t2c"), nc, nt);
        return new Protocol.Channel(rawIn, rawOut, t2c, c2t);
    }

    private void serve(Session session) throws IOException {
        Protocol.Channel ch = session.ch;
        while (running) {
            byte[] frame = ch.receive();
            if (frame.length < 9) throw new IOException("short frame");
            ByteBuffer b = ByteBuffer.wrap(frame);
            byte type = b.get();
            long id = b.getLong();
            if (type == Protocol.TYPE_TEXT || type == Protocol.TYPE_PNG) {
                synchronized (this) {
                    // A retry of a clip that already landed (its ack got lost) is only re-acked.
                    if (id != lastClipId) {
                        byte[] payload = Arrays.copyOfRange(frame, 9, frame.length);
                        try {
                            listener.onClip(type, id, payload);
                        } catch (Exception e) {
                            throw new IOException("could not apply clip: " + e, e);
                        }
                        lastClipId = id;
                    }
                }
            } else if (type == Protocol.TYPE_PING) {
                if (endsWith(frame, Protocol.DUPLEX_MARKER)) session.startSending();
            } else if (type == Protocol.TYPE_ACK) {
                // The PC confirming a clip this tablet sent. Acks are never acked.
                session.acked(id);
                continue;
            } else {
                throw new IOException("unknown frame type " + type);
            }
            ch.send(ByteBuffer.allocate(9).put(Protocol.TYPE_ACK).putLong(id).array());
        }
    }

    private static boolean endsWith(byte[] frame, byte[] suffix) {
        if (frame.length < 9 + suffix.length) return false;
        return Arrays.equals(Arrays.copyOfRange(frame, frame.length - suffix.length, frame.length), suffix);
    }

    /** Takes the clip for the PC, waiting up to waitMs for one. Drops one that has grown too old. */
    private Outgoing nextOutgoing(long waitMs) throws InterruptedException {
        synchronized (outLock) {
            if (outgoing == null) outLock.wait(waitMs);
            if (outgoing != null && System.currentTimeMillis() - outgoing.created > MAX_CLIP_AGE_MS) {
                listener.onLog("dropped clip " + outgoing.id + " for the PC: older than 10 minutes");
                outgoing = null;
            }
            return outgoing;
        }
    }

    private void delivered(Outgoing clip) {
        synchronized (outLock) {
            if (outgoing == clip) outgoing = null;
        }
        listener.onLog("delivered clip " + clip.id + " to the PC (" + clip.payload.length + " bytes)");
        listener.onSentToPc(clip.id);
    }

    /**
     * One authenticated connection. Its thread sends this tablet's clips over it, once
     * the PC shows it can read them. Static, like Watchdog: d8 34.0.0 crashes compiling
     * threads declared inside a non-static inner class.
     */
    private static final class Session extends Thread {
        final BridgeServer server;
        final Protocol.Channel ch;
        final Closeable connection;
        final boolean slow;
        private final Object ackLock = new Object();
        private long lastAck = Long.MIN_VALUE;
        private volatile boolean ended;
        private boolean sending;

        Session(BridgeServer server, Protocol.Channel ch, Closeable connection, boolean slow) {
            super("cb-send");
            setDaemon(true);
            this.server = server;
            this.ch = ch;
            this.connection = connection;
            this.slow = slow;
        }

        synchronized void startSending() {
            if (sending || ended) return;
            sending = true;
            start();
        }

        void acked(long id) {
            synchronized (ackLock) {
                lastAck = id;
                ackLock.notifyAll();
            }
        }

        void end() {
            synchronized (ackLock) {
                ended = true;
                ackLock.notifyAll();
            }
            synchronized (this) {
                if (sending) interrupt();
            }
        }

        private boolean isEnded() {
            synchronized (ackLock) {
                return ended;
            }
        }

        @Override
        public void run() {
            try {
                while (!isEnded()) {
                    Outgoing clip = server.nextOutgoing(1000);
                    if (clip == null) continue;
                    byte[] frame = ByteBuffer.allocate(9 + clip.payload.length)
                            .put(clip.type).putLong(clip.id).put(clip.payload).array();
                    ch.send(frame);
                    // Same allowances as the PC, plus time for the PC to set its clipboard.
                    long timeout = (slow ? 20_000 + clip.payload.length / 15 : 8000 + clip.payload.length / 256) + 5000;
                    if (!waitForAck(clip.id, timeout)) {
                        if (!isEnded()) {
                            server.listener.onLog("PC did not confirm clip " + clip.id + ", reconnecting");
                            closeQuietly(connection);
                        }
                        return;
                    }
                    server.delivered(clip);
                }
            } catch (InterruptedException ignored) {
                // The connection ended.
            } catch (IOException e) {
                server.listener.onLog("sending to the PC failed: " + e.getMessage());
                closeQuietly(connection);
            }
        }

        private boolean waitForAck(long id, long timeoutMs) throws InterruptedException {
            long deadline = System.currentTimeMillis() + timeoutMs;
            synchronized (ackLock) {
                while (lastAck != id) {
                    if (ended) return false;
                    long left = deadline - System.currentTimeMillis();
                    if (left <= 0) return false;
                    ackLock.wait(left);
                }
                return true;
            }
        }
    }

    /** Closes a connection that stays quiet too long. Only a proven PC earns the longer limit. */
    private static final class Watchdog extends Thread {
        private final Closeable connection;
        private volatile long deadline = System.currentTimeMillis() + HANDSHAKE_TIMEOUT_MS;
        private volatile boolean isAuthenticated;
        private volatile boolean finished;

        Watchdog(Closeable connection) {
            super("cb-watchdog");
            this.connection = connection;
            setDaemon(true);
        }

        void authenticated() {
            isAuthenticated = true;
            activity();
        }

        void activity() {
            if (isAuthenticated) deadline = System.currentTimeMillis() + IDLE_TIMEOUT_MS;
        }

        void finish() {
            finished = true;
            interrupt();
        }

        @Override
        public void run() {
            while (!finished) {
                if (System.currentTimeMillis() > deadline) {
                    closeQuietly(connection);
                    return;
                }
                try {
                    Thread.sleep(1000);
                } catch (InterruptedException e) {
                    return;
                }
            }
        }
    }

    /** Bytes still arriving count as activity, so a big image on a slow link isn't cut off. */
    private static final class WatchedInput extends FilterInputStream {
        private final Watchdog watchdog;

        WatchedInput(InputStream in, Watchdog watchdog) {
            super(in);
            this.watchdog = watchdog;
        }

        @Override
        public int read() throws IOException {
            int b = in.read();
            watchdog.activity();
            return b;
        }

        @Override
        public int read(byte[] b, int off, int len) throws IOException {
            int n = in.read(b, off, len);
            watchdog.activity();
            return n;
        }
    }

    private static void closeQuietly(Closeable c) {
        if (c == null) return;
        try {
            c.close();
        } catch (IOException ignored) {
        }
    }

    private static void sleep(long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        }
    }
}
