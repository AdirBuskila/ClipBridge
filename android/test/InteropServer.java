import com.clipbridge.BridgeServer;
import com.clipbridge.Protocol;

import java.io.File;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.Arrays;

/**
 * Runs the tablet's real networking code on a desktop JVM for pc/Tests.
 * Clips arriving from the PC print as CLIP lines. Files dropped into CB_OUTBOX_DIR are
 * sent to the PC, as if copied on the tablet: out-*.txt as text, out-*.png as an image.
 */
public class InteropServer {
    public static void main(String[] args) throws Exception {
        String code = args[0];
        int tcp = Integer.parseInt(args[1]);
        int udp = Integer.parseInt(args[2]);
        BridgeServer server = new BridgeServer(Protocol.deriveKey(code), tcp, udp, new BridgeServer.Listener() {
            @Override
            public void onClip(byte type, long id, byte[] payload) throws Exception {
                System.out.println("CLIP " + type + " " + id + " " + payload.length + " " + sha256(payload));
                System.out.flush();
                String saveDir = System.getenv("CB_SAVE_DIR");
                if (saveDir != null) {
                    java.nio.file.Files.write(java.nio.file.Paths.get(saveDir, "clip-" + id + (type == 2 ? ".png" : ".txt")), payload);
                }
            }

            @Override
            public void onConnectionChanged(boolean connected, String peer) {
                System.out.println(connected ? "CONNECTED" : "DISCONNECTED");
                System.out.flush();
            }

            @Override
            public void onSentToPc(long id) {
                System.out.println("SENT " + id);
                System.out.flush();
            }

            @Override
            public void onLog(String message) {
                System.out.println("LOG " + message);
                System.out.flush();
            }
        });
        server.start();

        String outbox = System.getenv("CB_OUTBOX_DIR");
        if (outbox != null) {
            Thread t = new Thread(() -> watchOutbox(server, new File(outbox)), "outbox");
            t.setDaemon(true);
            t.start();
        }

        Thread.sleep(300);
        System.out.println("READY");
        System.out.flush();
        Thread.currentThread().join();
    }

    private static void watchOutbox(BridgeServer server, File dir) {
        while (true) {
            try {
                File[] files = dir.listFiles((d, name) -> name.startsWith("out-") && !name.endsWith(".tmp"));
                if (files != null) {
                    Arrays.sort(files);
                    for (File f : files) {
                        byte[] payload = Files.readAllBytes(f.toPath());
                        byte type = f.getName().endsWith(".png") ? Protocol.TYPE_PNG : Protocol.TYPE_TEXT;
                        long id = server.sendToPc(type, payload);
                        System.out.println("QUEUED " + f.getName() + " " + id + " " + payload.length + " " + sha256(payload));
                        System.out.flush();
                        f.delete();
                    }
                }
                Thread.sleep(100);
            } catch (Exception e) {
                System.out.println("LOG outbox: " + e);
                System.out.flush();
            }
        }
    }

    private static String sha256(byte[] payload) throws Exception {
        StringBuilder hex = new StringBuilder();
        for (byte x : MessageDigest.getInstance("SHA-256").digest(payload)) {
            hex.append(String.format("%02x", x));
        }
        return hex.toString();
    }
}
