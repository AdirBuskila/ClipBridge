import com.clipbridge.BridgeServer;
import com.clipbridge.Protocol;

import java.security.MessageDigest;

/** Runs the tablet's real networking code on a desktop JVM for pc/Tests. */
public class InteropServer {
    public static void main(String[] args) throws Exception {
        String code = args[0];
        int tcp = Integer.parseInt(args[1]);
        int udp = Integer.parseInt(args[2]);
        BridgeServer server = new BridgeServer(Protocol.deriveKey(code), tcp, udp, new BridgeServer.Listener() {
            @Override
            public void onClip(byte type, long id, byte[] payload) throws Exception {
                StringBuilder hex = new StringBuilder();
                for (byte x : MessageDigest.getInstance("SHA-256").digest(payload)) {
                    hex.append(String.format("%02x", x));
                }
                System.out.println("CLIP " + type + " " + id + " " + payload.length + " " + hex);
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
            public void onLog(String message) {
                System.out.println("LOG " + message);
                System.out.flush();
            }
        });
        server.start();
        Thread.sleep(300);
        System.out.println("READY");
        System.out.flush();
        Thread.currentThread().join();
    }
}
