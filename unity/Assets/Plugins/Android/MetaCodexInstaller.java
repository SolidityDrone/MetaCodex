package com.pnncl.metacodex.install;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageInstaller;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Installs an APK built in MetaCodex's Linux with Android's own installer (a PackageInstaller session): the system
 * shows its confirmation and the user decides. Needs REQUEST_INSTALL_PACKAGES in the manifest and, once, the user's
 * "install unknown apps" switch for MetaCodex (openUnknownSourcesSettings). No developer mode, no adb.
 * The outcome is polled through status() (the C# side shows it and answers the control port).
 */
public final class MetaCodexInstaller {
    private static final String ACTION = "com.pnncl.metacodex.INSTALL_STATUS";
    private static volatile String status = "idle";
    private static BroadcastReceiver receiver;

    private MetaCodexInstaller() {
    }

    public static String status() {
        return status;
    }

    /** Whether the user lets MetaCodex install apps ("install unknown apps"). */
    public static boolean canInstall(Activity a) {
        return Build.VERSION.SDK_INT < 26 || a.getPackageManager().canRequestPackageInstalls();
    }

    /** Opens the "install unknown apps" switch for MetaCodex (Android's settings page). */
    public static String openUnknownSourcesSettings(Activity a) {
        try {
            a.startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + a.getPackageName()))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            return "ok";
        } catch (Exception e) {
            return "failure: no settings page for unknown apps: " + e;
        }
    }

    /** Starts installing the APK at `path` (a file the app can read); the result arrives in status(). */
    public static synchronized String install(Activity a, String path) {
        try {
            File f = new File(path);
            if (!f.isFile()) return status = "failure: no file " + path;
            ensureReceiver(a);
            PackageInstaller pi = a.getPackageManager().getPackageInstaller();
            PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
            int id = pi.createSession(params);
            PackageInstaller.Session s = pi.openSession(id);
            InputStream in = new FileInputStream(f);
            OutputStream out = s.openWrite("base.apk", 0, f.length());
            byte[] buf = new byte[1 << 16];
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
            s.fsync(out);
            out.close();
            in.close();
            Intent cb = new Intent(ACTION).setPackage(a.getPackageName());
            int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0);
            PendingIntent pending = PendingIntent.getBroadcast(a, id, cb, flags);
            s.commit(pending.getIntentSender());
            s.close();
            return status = "installing " + f.getName();
        } catch (Exception e) {
            return status = "failure: " + e;
        }
    }

    private static void ensureReceiver(Activity a) {
        if (receiver != null) return;
        receiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context ctx, Intent i) {
                int st = i.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
                String msg = i.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
                String pkg = i.getStringExtra(PackageInstaller.EXTRA_PACKAGE_NAME);
                if (st == PackageInstaller.STATUS_PENDING_USER_ACTION) {
                    Intent confirm = i.getParcelableExtra(Intent.EXTRA_INTENT);
                    status = "waiting for the user to confirm in the headset";
                    if (confirm != null) {
                        confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                        ctx.startActivity(confirm);
                    }
                } else if (st == PackageInstaller.STATUS_SUCCESS) {
                    status = "success: " + pkg;
                } else {
                    status = "failure (" + st + "): " + msg;
                }
            }
        };
        IntentFilter filter = new IntentFilter(ACTION);
        Context app = a.getApplicationContext();
        if (Build.VERSION.SDK_INT >= 33) app.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED);
        else app.registerReceiver(receiver, filter);
    }

    /** Starts an installed app by package name (its launcher activity). */
    public static String launch(Activity a, String pkg) {
        try {
            Intent i = a.getPackageManager().getLaunchIntentForPackage(pkg);
            if (i == null) return "failure: no launcher activity for " + pkg;
            i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            a.startActivity(i);
            return "ok";
        } catch (Exception e) {
            return "failure: " + e;
        }
    }
}
