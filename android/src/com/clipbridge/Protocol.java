package com.clipbridge;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Locale;

import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.SecretKeyFactory;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.PBEKeySpec;
import javax.crypto.spec.SecretKeySpec;

/**
 * Wire protocol shared with the PC app (pc/Core/Proto.cs). No Android imports,
 * so it can also run on a desktop JVM for the interop tests.
 */
public final class Protocol {
    public static final int TCP_PORT = 47821;
    public static final int UDP_PORT = 47822;

    public static final byte TYPE_TEXT = 1;
    public static final byte TYPE_PNG = 2;
    public static final byte TYPE_PING = 3;
    public static final byte TYPE_ACK = 4;

    public static final int MAX_FRAME = 128 * 1024 * 1024;

    /** Appended to the PC's pings when it can read clips the tablet sends. */
    static final byte[] DUPLEX_MARKER = ascii("CB-DUPLEX");

    static final byte[] HELLO = ascii("CB1\n");
    static final byte[] DISCOVER_MAGIC = ascii("CBD1");
    static final byte[] REPLY_MAGIC = ascii("CBR1");

    /** No 0/O, 1/I/L, so the code is easy to read off a screen. */
    private static final String CODE_ALPHABET = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private static final int LEGACY_CODE_LENGTH = 16;
    private static final int KEY_ITERATIONS = 300_000;

    private Protocol() {}

    static byte[] ascii(String s) {
        return s.getBytes(StandardCharsets.US_ASCII);
    }

    /** 8 characters from a 31-letter alphabet, about 40 random bits. */
    public static String generateCode(SecureRandom random) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 8; i++) {
            if (i > 0 && i % 4 == 0) sb.append('-');
            sb.append(CODE_ALPHABET.charAt(random.nextInt(CODE_ALPHABET.length())));
        }
        return sb.toString();
    }

    public static String normalizeCode(String code) {
        StringBuilder sb = new StringBuilder();
        for (char c : code.toUpperCase(Locale.ROOT).toCharArray()) {
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.append(c);
        }
        return sb.toString();
    }

    /**
     * Short codes go through a deliberately slow derivation, because anyone who can
     * reach the tablet could otherwise test guesses against a handshake at full speed.
     * Old 16-character codes keep their original derivation, so an existing pairing
     * keeps working until the new code is entered.
     */
    public static byte[] deriveKey(String code) {
        String normalized = normalizeCode(code);
        try {
            if (normalized.length() == LEGACY_CODE_LENGTH) {
                MessageDigest sha = MessageDigest.getInstance("SHA-256");
                return sha.digest(("ClipBridge-v1:" + normalized).getBytes(StandardCharsets.UTF_8));
            }
            SecretKeyFactory factory = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256");
            PBEKeySpec spec = new PBEKeySpec(normalized.toCharArray(),
                    ascii("ClipBridge-v2-pairing"), KEY_ITERATIONS, 256);
            return factory.generateSecret(spec).getEncoded();
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException(e);
        }
    }

    public static byte[] hmac(byte[] key, byte[]... parts) {
        try {
            Mac mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(key, "HmacSHA256"));
            for (byte[] p : parts) mac.update(p);
            return mac.doFinal();
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException(e);
        }
    }

    /** One AES-256-GCM encrypted, length-prefixed frame stream in each direction. */
    public static final class Channel {
        private final DataInputStream in;
        private final DataOutputStream out;
        private final SecretKeySpec sendKey;
        private final SecretKeySpec recvKey;
        private long sendCounter;
        private long recvCounter;

        Channel(InputStream in, OutputStream out, byte[] sendKey, byte[] recvKey) {
            this.in = new DataInputStream(in);
            this.out = new DataOutputStream(out);
            this.sendKey = new SecretKeySpec(sendKey, "AES");
            this.recvKey = new SecretKeySpec(recvKey, "AES");
        }

        private static GCMParameterSpec nonce(long counter) {
            byte[] n = new byte[12];
            ByteBuffer.wrap(n).putLong(4, counter);
            return new GCMParameterSpec(128, n);
        }

        public synchronized void send(byte[] plain) throws IOException {
            try {
                Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
                c.init(Cipher.ENCRYPT_MODE, sendKey, nonce(sendCounter++));
                byte[] ct = c.doFinal(plain);
                out.writeInt(ct.length);
                out.write(ct);
                out.flush();
            } catch (GeneralSecurityException e) {
                throw new IOException(e);
            }
        }

        /** Throws IOException on a dropped link or on any frame that fails authentication. */
        public byte[] receive() throws IOException {
            int len = in.readInt();
            if (len < 16 || len > MAX_FRAME) throw new IOException("bad frame length " + len);
            byte[] ct = new byte[len];
            in.readFully(ct);
            try {
                Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
                c.init(Cipher.DECRYPT_MODE, recvKey, nonce(recvCounter++));
                return c.doFinal(ct);
            } catch (GeneralSecurityException e) {
                throw new IOException("frame failed authentication", e);
            }
        }
    }
}
