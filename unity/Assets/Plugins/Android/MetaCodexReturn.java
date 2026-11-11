package com.pnncl.metacodex.install;

import android.app.Activity;
import android.content.Intent;
import android.os.Build;

import java.io.File;
import java.io.FileOutputStream;

/**
 * The way back from apps MetaCodex launched (apps on the native XR layer, mcx_xr): their menu's "Back to MetaCodex"
 * and "Report to MetaCodex" start MetaCodex again with com.metacodex.extra.RETURN_FROM (their package),
 * .LAUNCH_ID (the id MetaCodex gave that launch), .APP_LABEL, .APP_VERSION and, for a report, .REPORT, .REPORT_LOG
 * (the app's last log lines) and .REPORT_PNG (a picture of the app). UnityPlayerActivity.onNewIntent keeps the newest
 * intent (setIntent), so take() reads it once on resume: it writes the picture and the log next to each other in
 * `dir` and removes the extras so a later resume does not see them again. The C# side (ShellReturn) accepts only
 * launch ids it gave out; on Android 14+ it also gets the sender's package when the app shared its identity.
 */
public final class MetaCodexReturn {
    private static final String P = "com.metacodex.extra.";
    private static final String[] KEYS = {"RETURN_FROM", "LAUNCH_ID", "APP_LABEL", "APP_VERSION", "REPORT", "REPORT_LOG", "REPORT_PNG"};

    private MetaCodexReturn() {
    }

    /** "" when the intent is no return; else a JSON object (from, launchId, label, version, report, png, log, launchedFrom). */
    public static String take(Activity a, String dir) {
        try {
            Intent i = a.getIntent();
            if (i == null) return "";
            String from = i.getStringExtra(P + "RETURN_FROM");
            if (from == null || from.isEmpty()) return "";
            String launchId = i.getStringExtra(P + "LAUNCH_ID");
            boolean report = "1".equals(i.getStringExtra(P + "REPORT"));
            String png = "", log = "";
            String stamp = new java.text.SimpleDateFormat("yyyyMMdd-HHmmss", java.util.Locale.ROOT).format(new java.util.Date());
            if (report && dir != null) {
                new File(dir).mkdirs();
                byte[] pic = i.getByteArrayExtra(P + "REPORT_PNG");
                if (pic != null && pic.length > 8 && pic[0] == (byte) 0x89 && pic[1] == 'P') {
                    File f = new File(dir, "report-" + stamp + ".png");
                    try (FileOutputStream o = new FileOutputStream(f)) { o.write(pic); }
                    png = f.getAbsolutePath();
                }
                String text = i.getStringExtra(P + "REPORT_LOG");
                if (text != null && !text.isEmpty()) {
                    File f = new File(dir, "report-" + stamp + ".log.txt");
                    try (FileOutputStream o = new FileOutputStream(f)) { o.write(text.getBytes("UTF-8")); }
                    log = f.getAbsolutePath();
                }
            }
            String launchedFrom = "";
            if (Build.VERSION.SDK_INT >= 34) {
                try {
                    String lf = a.getLaunchedFromPackage();
                    if (lf != null) launchedFrom = lf;
                } catch (Throwable ignored) {
                    // not available
                }
            }
            org.json.JSONObject j = new org.json.JSONObject();
            j.put("from", from);
            j.put("launchId", launchId != null ? launchId : "");
            j.put("label", nz(i.getStringExtra(P + "APP_LABEL")));
            j.put("version", nz(i.getStringExtra(P + "APP_VERSION")));
            j.put("report", report);
            j.put("png", png);
            j.put("log", log);
            j.put("launchedFrom", launchedFrom);
            for (String k : KEYS) i.removeExtra(P + k);
            a.setIntent(i);
            return j.toString();
        } catch (Exception e) {
            return "{\"error\":\"" + e.toString().replace("\"", "'") + "\"}";
        }
    }

    private static String nz(String s) {
        return s != null ? s : "";
    }
}
