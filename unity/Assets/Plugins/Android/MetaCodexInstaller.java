package com.pnncl.metacodex.install;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageManager;
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
 * An update of an app that MetaCodex installed itself asks nothing when Android allows it (API 31+:
 * USER_ACTION_NOT_REQUIRED + UPDATE_PACKAGES_WITHOUT_USER_ACTION; the app must target API 31+ on Android 14).
 * The outcome of the latest install is polled through status() (the C# side answers the control port).
 */
public final class MetaCodexInstaller {
    private static final String ACTION = "com.pnncl.metacodex.INSTALL_STATUS";
    private static final String EXTRA_SESSION = "com.pnncl.metacodex.SESSION";
    private static volatile String status = "idle";
    /** The latest session: results of older ones (a dialog answered late) do not overwrite status(). */
    private static volatile int session = -1;
    private static volatile boolean askedUser;
    private static BroadcastReceiver receiver;

    private MetaCodexInstaller() {
    }

    public static String status() {
        return status;
    }

    /** Whether this build declares REQUEST_INSTALL_PACKAGES (Store builds must not: Meta prohibits it). */
    public static boolean declared(Activity a) {
        try {
            String[] req = a.getPackageManager().getPackageInfo(a.getPackageName(), PackageManager.GET_PERMISSIONS).requestedPermissions;
            if (req != null) for (String p : req) if ("android.permission.REQUEST_INSTALL_PACKAGES".equals(p)) return true;
        } catch (Exception e) {
            // unknown: treat as not declared
        }
        return false;
    }

    /** Whether the user lets MetaCodex install apps ("install unknown apps"). */
    public static boolean canInstall(Activity a) {
        return Build.VERSION.SDK_INT < 26 || a.getPackageManager().canRequestPackageInstalls();
    }

    /** yes | no: ... (the switch is off) | unavailable: ... (this build cannot install apps). Opens nothing. */
    public static String canInstallText(Activity a) {
        if (!declared(a)) return "unavailable: this MetaCodex build cannot install apps (no REQUEST_INSTALL_PACKAGES; Store builds leave it out)";
        return canInstall(a) ? "yes" : "no: 'Install unknown apps' is off for MetaCodex (system install-apk opens that switch)";
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

    /**
     * Starts installing the APK at `path` (a file the app can read) and returns at once: the copy into the session
     * runs on its own thread (a big APK must not stall the frame loop). The result arrives in status().
     */
    public static synchronized String install(final Activity a, String path) {
        final File f = new File(path);
        if (!f.isFile()) return status = "failure: no file " + path;
        try {
            ensureReceiver(a);
            final PackageInstaller pi = a.getPackageManager().getPackageInstaller();
            PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
            params.setSize(f.length());
            if (Build.VERSION.SDK_INT >= 31) params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
            final int id = pi.createSession(params);
            session = id;
            askedUser = false;
            status = "installing " + f.getName() + " (session " + id + ")";
            final String pkg = a.getPackageName();
            final Context app = a.getApplicationContext();
            new Thread(new Runnable() {
                @Override
                public void run() {
                    PackageInstaller.Session s = null;
                    try {
                        s = pi.openSession(id);
                        try (InputStream in = new FileInputStream(f); OutputStream out = s.openWrite("base.apk", 0, f.length())) {
                            byte[] buf = new byte[1 << 16];
                            int n;
                            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                            s.fsync(out);
                        }
                        Intent cb = new Intent(ACTION).setPackage(pkg).putExtra(EXTRA_SESSION, id);
                        int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0);
                        s.commit(PendingIntent.getBroadcast(app, id, cb, flags).getIntentSender());
                    } catch (Exception e) {
                        if (s != null) {
                            try {
                                s.abandon();
                            } catch (Exception ignored) {
                                // already gone
                            }
                        }
                        if (session == id) status = "failure: " + e;
                    } finally {
                        if (s != null) {
                            try {
                                s.close();
                            } catch (Exception ignored) {
                                // abandoned already
                            }
                        }
                    }
                }
            }, "mcx-install").start();
            return status;
        } catch (Exception e) {
            return status = "failure: " + e;
        }
    }

    private static void ensureReceiver(Activity a) {
        if (receiver != null) return;
        receiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context ctx, Intent i) {
                int sid = i.getIntExtra(EXTRA_SESSION, i.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1));
                if (sid != session) return; // an older install answered late: status() is about the latest one
                int st = i.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
                String msg = i.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
                String pkg = i.getStringExtra(PackageInstaller.EXTRA_PACKAGE_NAME);
                if (st == PackageInstaller.STATUS_PENDING_USER_ACTION) {
                    Intent confirm = i.getParcelableExtra(Intent.EXTRA_INTENT);
                    askedUser = true;
                    status = "waiting for the user to confirm in the headset (session " + sid + ")";
                    if (confirm != null) {
                        confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                        ctx.startActivity(confirm);
                    }
                } else if (st == PackageInstaller.STATUS_SUCCESS) {
                    status = "success: " + pkg + (askedUser ? "" : " (no prompt: an update of an app MetaCodex installed)");
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

    /** installed: pkg version (code), installer ... | not installed: pkg. Apps with a launcher activity are visible. */
    public static String info(Activity a, String pkg) {
        try {
            PackageManager pm = a.getPackageManager();
            PackageInfo p = pm.getPackageInfo(pkg, 0);
            long code = Build.VERSION.SDK_INT >= 28 ? p.getLongVersionCode() : p.versionCode;
            String installer = Build.VERSION.SDK_INT >= 30 ? pm.getInstallSourceInfo(pkg).getInstallingPackageName() : pm.getInstallerPackageName(pkg);
            return "installed: " + pkg + " " + p.versionName + " (" + code + "), installer " + (installer != null ? installer : "adb or unknown")
                    + ", updated " + new java.util.Date(p.lastUpdateTime);
        } catch (PackageManager.NameNotFoundException e) {
            return "not installed: " + pkg;
        } catch (Exception e) {
            return "failure: " + e;
        }
    }

    /** Starts an installed app by package name (its launcher activity). */
    public static String launch(Activity a, String pkg) {
        return launch(a, pkg, null);
    }

    /**
     * Starts an installed app and tells it who started it: apps built on the native XR layer (mcx_xr) read
     * com.metacodex.extra.CALLER and .LAUNCH_ID and offer "Back to MetaCodex" / "Report to MetaCodex" in their menu,
     * which start MetaCodex again with the same launch id (MetaCodexReturn checks it). The id goes only to this app.
     */
    public static String launch(Activity a, String pkg, String launchId) {
        try {
            Intent i = a.getPackageManager().getLaunchIntentForPackage(pkg);
            if (i == null) return "failure: no launcher activity for " + pkg + " (not installed?)";
            i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            i.putExtra("com.metacodex.extra.CALLER", a.getPackageName());
            if (launchId != null && !launchId.isEmpty()) i.putExtra("com.metacodex.extra.LAUNCH_ID", launchId);
            a.startActivity(i);
            return "ok";
        } catch (Exception e) {
            return "failure: " + e;
        }
    }
}
