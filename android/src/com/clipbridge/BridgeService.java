package com.clipbridge;

import android.Manifest;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothServerSocket;
import android.bluetooth.BluetoothSocket;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.util.Log;

import java.io.File;
import java.io.FileOutputStream;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.SocketException;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.Enumeration;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

/** Stays running in the background and puts whatever the PC sends on the clipboard. */
public class BridgeService extends Service implements BridgeServer.Listener {
    private static final String TAG = "ClipBridge";
    private static final String CHANNEL = "status";
    private static final int NOTIFICATION_ID = 1;
    private static final int KEEP_IMAGES = 3;
    /** Must match pc/App/BluetoothLink.cs. */
    private static final UUID BLUETOOTH_SERVICE_ID = UUID.fromString("7c1b6f2e-3a54-4d8e-9f10-c11bb21d6e01");

    /** Read by MainActivity. */
    static volatile boolean connected;
    static volatile String connectedVia = "";
    static volatile String lastClip = "Nothing received yet";
    static volatile String bluetoothState = "Bluetooth: starting";

    private volatile boolean stopping;
    private volatile BluetoothServerSocket bluetoothListener;

    private final Handler main = new Handler(Looper.getMainLooper());
    private BridgeServer server;
    private WifiManager.WifiLock wifiLock;
    private WifiManager.MulticastLock multicastLock;
    private PowerManager.WakeLock wakeLock;
    private volatile Uri lastImage;

    static String pairingCode(Context context) {
        SharedPreferences prefs = context.getSharedPreferences("clipbridge", MODE_PRIVATE);
        // "code" held the old 16-character format; "shortCode" replaces it.
        String code = prefs.getString("shortCode", null);
        if (code == null) {
            code = Protocol.generateCode(new SecureRandom());
            prefs.edit().putString("shortCode", code).remove("code").commit();
        }
        return code;
    }

    static List<String> localAddresses() {
        List<String> result = new ArrayList<>();
        try {
            Enumeration<NetworkInterface> nics = NetworkInterface.getNetworkInterfaces();
            if (nics == null) return result;
            for (NetworkInterface nic : Collections.list(nics)) {
                if (!nic.isUp() || nic.isLoopback()) continue;
                for (InetAddress a : Collections.list(nic.getInetAddresses())) {
                    if (a.getAddress().length == 4) result.add(a.getHostAddress());
                }
            }
        } catch (SocketException ignored) {
        }
        return result;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        NotificationChannel channel = new NotificationChannel(CHANNEL, "ClipBridge status", NotificationManager.IMPORTANCE_LOW);
        channel.setShowBadge(false);
        getSystemService(NotificationManager.class).createNotificationChannel(channel);
        startForeground(NOTIFICATION_ID, buildNotification());

        WifiManager wifi = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        wifiLock = wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "clipbridge:wifi");
        wifiLock.setReferenceCounted(false);
        wifiLock.acquire();
        multicastLock = wifi.createMulticastLock("clipbridge:discovery");
        multicastLock.setReferenceCounted(false);
        multicastLock.acquire();
        wakeLock = getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "clipbridge:clip");
        wakeLock.setReferenceCounted(false);

        server = new BridgeServer(Protocol.deriveKey(pairingCode(this)), Protocol.TCP_PORT, Protocol.UDP_PORT, this);
        server.start();
        new Thread(this::bluetoothLoop, "cb-bluetooth").start();
    }

    /** Listens over Bluetooth too, for networks that block devices from reaching each other. */
    private void bluetoothLoop() {
        while (!stopping) {
            BluetoothServerSocket listen = null;
            try {
                if (Build.VERSION.SDK_INT >= 31
                        && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                    bluetoothState = "Bluetooth: permission needed";
                    Thread.sleep(2000);
                    continue;
                }
                BluetoothAdapter adapter = getSystemService(BluetoothManager.class).getAdapter();
                if (adapter == null || !adapter.isEnabled()) {
                    bluetoothState = "Bluetooth: turned off";
                    Thread.sleep(3000);
                    continue;
                }
                listen = adapter.listenUsingRfcommWithServiceRecord("ClipBridge", BLUETOOTH_SERVICE_ID);
                bluetoothListener = listen;
                bluetoothState = "Bluetooth: ready";
                while (!stopping) {
                    BluetoothSocket s = listen.accept();
                    new Thread(() -> serveBluetooth(s), "cb-bt-conn").start();
                }
            } catch (InterruptedException e) {
                return;
            } catch (Exception e) {
                if (stopping) return;
                bluetoothState = "Bluetooth: restarting";
                Log.i(TAG, "bluetooth listener: " + e);
                try {
                    Thread.sleep(3000);
                } catch (InterruptedException ie) {
                    return;
                }
            } finally {
                if (listen != null) {
                    try {
                        listen.close();
                    } catch (Exception ignored) {
                    }
                }
            }
        }
    }

    private void serveBluetooth(BluetoothSocket s) {
        try {
            server.handleConnection(s.getInputStream(), s.getOutputStream(), s, "Bluetooth");
        } catch (Exception e) {
            Log.i(TAG, "bluetooth connection: " + e);
            try {
                s.close();
            } catch (Exception ignored) {
            }
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        stopping = true;
        BluetoothServerSocket listen = bluetoothListener;
        if (listen != null) {
            try {
                listen.close();
            } catch (Exception ignored) {
            }
        }
        server.stop();
        wifiLock.release();
        multicastLock.release();
        if (wakeLock.isHeld()) wakeLock.release();
        connected = false;
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onClip(byte type, long id, byte[] payload) throws Exception {
        // The ack only goes out after this returns, so hold the CPU until the clip landed.
        wakeLock.acquire(60_000);
        try {
            ClipData clip;
            if (type == Protocol.TYPE_TEXT) {
                clip = ClipData.newPlainText("ClipBridge", new String(payload, StandardCharsets.UTF_8));
                lastImage = null;
                lastClip = "Text, " + payload.length + " bytes";
            } else if (type == Protocol.TYPE_PNG) {
                File file = new File(ClipProvider.clipDir(this), Long.toUnsignedString(id) + ".png");
                try (FileOutputStream out = new FileOutputStream(file)) {
                    out.write(payload);
                }
                Uri uri = ClipProvider.uriFor(file);
                clip = ClipData.newUri(getContentResolver(), "Screenshot", uri);
                lastImage = uri;
                lastClip = "Screenshot, " + (payload.length / 1024) + " KB";
                pruneImages();
            } else {
                throw new IllegalArgumentException("unknown clip type " + type);
            }
            setClipboard(clip);
            updateNotification();
        } finally {
            if (wakeLock.isHeld()) wakeLock.release();
        }
    }

    /** Apps in the background may write the clipboard, and only reading it is restricted. */
    private void setClipboard(ClipData clip) throws Exception {
        CountDownLatch done = new CountDownLatch(1);
        AtomicReference<Exception> error = new AtomicReference<>();
        main.post(() -> {
            try {
                getSystemService(ClipboardManager.class).setPrimaryClip(clip);
            } catch (Exception e) {
                error.set(e);
            }
            done.countDown();
        });
        if (!done.await(10, TimeUnit.SECONDS)) throw new IllegalStateException("clipboard timed out");
        if (error.get() != null) throw error.get();
    }

    private void pruneImages() {
        File[] files = ClipProvider.clipDir(this).listFiles();
        if (files == null || files.length <= KEEP_IMAGES) return;
        Arrays.sort(files, Comparator.comparingLong(File::lastModified).reversed());
        for (int i = KEEP_IMAGES; i < files.length; i++) files[i].delete();
    }

    @Override
    public void onConnectionChanged(boolean isConnected, String peer) {
        connected = isConnected;
        connectedVia = "Bluetooth".equals(peer) ? " (Bluetooth)" : " (Wi-Fi)";
        Log.i(TAG, (isConnected ? "connected: " : "disconnected: ") + peer);
        updateNotification();
    }

    @Override
    public void onLog(String message) {
        Log.i(TAG, message);
    }

    private void updateNotification() {
        getSystemService(NotificationManager.class).notify(NOTIFICATION_ID, buildNotification());
    }

    private Notification buildNotification() {
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class),
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification.Builder b = new Notification.Builder(this, CHANNEL)
                .setSmallIcon(R.drawable.ic_app)
                .setContentTitle(connected ? "Connected to your PC" + connectedVia : "Waiting for your PC")
                .setContentText(lastClip)
                .setContentIntent(open)
                .setOnlyAlertOnce(true)
                .setOngoing(true);
        Uri image = lastImage;
        if (image != null) {
            // For apps that refuse pasted images.
            Intent send = new Intent(Intent.ACTION_SEND)
                    .setType("image/png")
                    .putExtra(Intent.EXTRA_STREAM, image)
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            Intent chooser = Intent.createChooser(send, "Share screenshot")
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            PendingIntent share = PendingIntent.getActivity(this, 1, chooser,
                    PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
            b.addAction(new Notification.Action.Builder(null, "Share image", share).build());
        }
        return b.build();
    }
}
