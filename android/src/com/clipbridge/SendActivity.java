package com.clipbridge;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipDescription;
import android.content.ClipboardManager;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.net.Uri;
import android.os.Bundle;
import android.widget.Toast;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/**
 * Invisible. Sends the clipboard, a shared item or selected text to the PC, then closes.
 * Android only lets the app in focus read the clipboard, so the notification button and
 * the Quick Settings tile open this activity, and it reads once it has focus.
 */
public class SendActivity extends Activity {
    static final String ACTION_SEND_CLIPBOARD = "com.clipbridge.SEND_CLIPBOARD";
    /** Same limit as the PC. */
    private static final int MAX_BYTES = 100 * 1024 * 1024;

    private boolean readClipboard;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Intent intent = getIntent();
        String action = intent.getAction();
        if (Intent.ACTION_PROCESS_TEXT.equals(action)) {
            // "Send to PC" in the menu that appears over selected text.
            sendText(intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT));
            finish();
        } else if (Intent.ACTION_SEND.equals(action)) {
            Uri stream = intent.getParcelableExtra(Intent.EXTRA_STREAM);
            String type = intent.getType();
            if (stream != null && type != null && type.startsWith("image/")) {
                sendImage(stream);
            } else {
                sendText(intent.getCharSequenceExtra(Intent.EXTRA_TEXT));
                finish();
            }
        } else if (!ACTION_SEND_CLIPBOARD.equals(action)) {
            finish();
        }
        // ACTION_SEND_CLIPBOARD waits for onWindowFocusChanged.
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (!hasFocus || readClipboard || !ACTION_SEND_CLIPBOARD.equals(getIntent().getAction())) return;
        readClipboard = true;

        ClipData clip = getSystemService(ClipboardManager.class).getPrimaryClip();
        if (clip == null || clip.getItemCount() == 0) {
            toast("The clipboard is empty");
            finish();
            return;
        }
        ClipData.Item item = clip.getItemAt(0);
        Uri uri = item.getUri();
        if (uri != null && isImage(uri, clip.getDescription())) {
            sendImage(uri);
            return;
        }
        CharSequence text = item.getText();
        if (text == null) text = item.coerceToText(this);
        sendText(text);
        finish();
    }

    private boolean isImage(Uri uri, ClipDescription description) {
        if (description != null && description.hasMimeType("image/*")) return true;
        String type = getContentResolver().getType(uri);
        return type != null && type.startsWith("image/");
    }

    private void sendText(CharSequence text) {
        if (text == null || text.length() == 0) {
            toast("Nothing to send");
            return;
        }
        byte[] payload = text.toString().getBytes(StandardCharsets.UTF_8);
        if (payload.length > MAX_BYTES) {
            toast("Too big to send");
            return;
        }
        toast(BridgeService.sendToPc(this, Protocol.TYPE_TEXT, payload, "Text, " + payload.length + " bytes"));
    }

    /** Reads off the main thread, and only closes afterwards: access to a shared image ends with this activity. */
    private void sendImage(Uri uri) {
        new Thread(() -> {
            byte[] payload;
            try {
                payload = readImage(uri);
            } catch (Exception e) {
                payload = null;
            }
            byte[] result = payload;
            runOnUiThread(() -> {
                if (result == null) {
                    toast("Couldn't read that image");
                } else if (result.length > MAX_BYTES) {
                    toast("Too big to send");
                } else {
                    toast(BridgeService.sendToPc(this, Protocol.TYPE_PNG, result, "Image, " + (result.length / 1024) + " KB"));
                }
                finish();
            });
        }, "cb-read-image").start();
    }

    /** PNG, JPEG, GIF and BMP go as they are, since Windows reads them. Others become PNG. */
    private byte[] readImage(Uri uri) throws IOException {
        String type = getContentResolver().getType(uri);
        byte[] bytes;
        try (InputStream in = getContentResolver().openInputStream(uri)) {
            if (in == null) throw new IOException("no data");
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buf = new byte[64 * 1024];
            int n;
            while ((n = in.read(buf)) > 0) {
                out.write(buf, 0, n);
                if (out.size() > MAX_BYTES) return out.toByteArray();
            }
            bytes = out.toByteArray();
        }
        if (type != null && (type.equals("image/png") || type.equals("image/jpeg")
                || type.equals("image/gif") || type.equals("image/bmp"))) {
            return bytes;
        }
        Bitmap bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.length);
        if (bitmap == null) throw new IOException("not an image Android can read");
        ByteArrayOutputStream png = new ByteArrayOutputStream();
        bitmap.compress(Bitmap.CompressFormat.PNG, 100, png);
        bitmap.recycle();
        return png.toByteArray();
    }

    private void toast(String message) {
        Toast.makeText(getApplicationContext(), message, Toast.LENGTH_SHORT).show();
    }
}
