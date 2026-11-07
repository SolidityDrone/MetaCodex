package com.pnncl.metacodex.vault;

import android.content.Context;
import android.os.Build;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyInfo;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.SecretKeyFactory;
import javax.crypto.spec.GCMParameterSpec;

/**
 * The key of MetaCodex's secret vault: AES-256-GCM in the Android Keystore (StrongBox when the device has it,
 * otherwise the TEE). The key never leaves secure hardware; this class only seals and opens small values for
 * the C# vault (SecretVault.cs), which stores the sealed values in the app's no-backup folder.
 *
 * Strings cross JNI as base64 so no byte[] marshalling is involved. Every call returns null on failure and
 * keeps the reason in lastError().
 */
public final class MetaCodexVault {
    private static final String STORE = "AndroidKeyStore";
    private static final String ALIAS = "metacodex.vault.v1";
    private static final int IV_BYTES = 12;
    private static final int TAG_BITS = 128;
    private static volatile String lastError = "";

    private MetaCodexVault() {
    }

    private static synchronized SecretKey key() throws Exception {
        KeyStore ks = KeyStore.getInstance(STORE);
        ks.load(null);
        if (ks.containsAlias(ALIAS)) {
            KeyStore.Entry e = ks.getEntry(ALIAS, null);
            if (e instanceof KeyStore.SecretKeyEntry) {
                return ((KeyStore.SecretKeyEntry) e).getSecretKey();
            }
            ks.deleteEntry(ALIAS);
        }
        KeyGenerator kg = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, STORE);
        if (Build.VERSION.SDK_INT >= 28) {
            try {
                kg.init(spec(true));
                return kg.generateKey();
            } catch (Exception noStrongBox) {
                // StrongBoxUnavailableException (or a vendor variant): the TEE key below is the norm on headsets
            }
        }
        kg.init(spec(false));
        return kg.generateKey();
    }

    private static KeyGenParameterSpec spec(boolean strongBox) {
        KeyGenParameterSpec.Builder b = new KeyGenParameterSpec.Builder(ALIAS,
                KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .setRandomizedEncryptionRequired(true);
        if (strongBox && Build.VERSION.SDK_INT >= 28) {
            b.setIsStrongBoxBacked(true);
        }
        return b.build();
    }

    /** base64(plaintext), associated data -> base64(iv | ciphertext | tag), or null. */
    public static String seal(String plainB64, String aad) {
        try {
            Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
            c.init(Cipher.ENCRYPT_MODE, key()); // the keystore picks a fresh random IV
            if (aad != null) {
                c.updateAAD(aad.getBytes(StandardCharsets.UTF_8));
            }
            byte[] iv = c.getIV();
            byte[] ct = c.doFinal(Base64.decode(plainB64, Base64.NO_WRAP));
            if (iv == null || iv.length != IV_BYTES) {
                throw new IllegalStateException("unexpected IV length");
            }
            byte[] out = new byte[iv.length + ct.length];
            System.arraycopy(iv, 0, out, 0, iv.length);
            System.arraycopy(ct, 0, out, iv.length, ct.length);
            return Base64.encodeToString(out, Base64.NO_WRAP);
        } catch (Exception e) {
            lastError = e.toString();
            return null;
        }
    }

    /** base64(iv | ciphertext | tag), associated data -> base64(plaintext), or null (wrong key, tampered, other name). */
    public static String open(String sealedB64, String aad) {
        try {
            byte[] all = Base64.decode(sealedB64, Base64.NO_WRAP);
            if (all.length < IV_BYTES + TAG_BITS / 8) {
                throw new IllegalArgumentException("sealed value too short");
            }
            Cipher c = Cipher.getInstance("AES/GCM/NoPadding");
            c.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(TAG_BITS, all, 0, IV_BYTES));
            if (aad != null) {
                c.updateAAD(aad.getBytes(StandardCharsets.UTF_8));
            }
            byte[] plain = c.doFinal(all, IV_BYTES, all.length - IV_BYTES);
            return Base64.encodeToString(plain, Base64.NO_WRAP);
        } catch (Exception e) {
            lastError = e.toString();
            return null;
        }
    }

    /** Where the key lives: "strongbox", "tee", "software", or "unknown: ...". */
    public static String securityLevel() {
        try {
            SecretKey k = key();
            SecretKeyFactory f = SecretKeyFactory.getInstance(k.getAlgorithm(), STORE);
            KeyInfo info = (KeyInfo) f.getKeySpec(k, KeyInfo.class);
            if (Build.VERSION.SDK_INT >= 31) {
                int level = info.getSecurityLevel();
                if (level == KeyProperties.SECURITY_LEVEL_STRONGBOX) return "strongbox";
                if (level == KeyProperties.SECURITY_LEVEL_TRUSTED_ENVIRONMENT) return "tee";
                if (level == KeyProperties.SECURITY_LEVEL_SOFTWARE) return "software";
                return "level " + level;
            }
            return info.isInsideSecureHardware() ? "tee" : "software";
        } catch (Exception e) {
            lastError = e.toString();
            return "unknown: " + e.getMessage();
        }
    }

    /** The app's no-backup folder (never copied by Android backup or device transfer). */
    public static String noBackupDir(Context ctx) {
        return ctx.getNoBackupFilesDir().getAbsolutePath();
    }

    /** Deletes the key: every sealed value becomes unreadable (vault reset). */
    public static boolean destroy() {
        try {
            KeyStore ks = KeyStore.getInstance(STORE);
            ks.load(null);
            if (ks.containsAlias(ALIAS)) {
                ks.deleteEntry(ALIAS);
            }
            return true;
        } catch (Exception e) {
            lastError = e.toString();
            return false;
        }
    }

    public static String lastError() {
        return lastError;
    }
}
