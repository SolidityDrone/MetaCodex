package com.metacodex.example.zipper;

import android.app.Activity;
import android.app.ActivityOptions;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.res.ColorStateList;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.io.ByteArrayOutputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.Date;
import java.util.Locale;
import java.util.concurrent.LinkedBlockingQueue;

/**
 * MetaCodex's part of a 2D panel app (keep this file; the skill quest-panel-apps explains it):
 * <ul>
 * <li>{@link #log}: a line to logcat and to MetaCodex (127.0.0.1:47950, what `mcx-app logs` shows), sent off the UI
 * thread; the last 200 lines are kept for a report.</li>
 * <li>A crash writes its stack trace to that log before the app closes (`mcx-app logs` shows it).</li>
 * <li>{@link #start}: reads who launched the app; {@link #topBar} has BACK TO METACODEX and REPORT when MetaCodex did.
 * Back starts MetaCodex (it brings the thread that launched the app forward) and closes this app; Report also sends a
 * picture of the window and the last 60 log lines, which arrive in that thread as a draft.</li>
 * <li>The look: a dark theme readable in a headset window, 18sp text, 60dp buttons with pressed, hovered and focused
 * states (Meta's rules for 2D apps: targets at least 48dp, 60dp for primary ones, contrast 4.5:1, no pure black or
 * white).</li>
 * </ul>
 */
final class Mcx {
    // colours (contrast against BG: TEXT 13:1, DIM 6:1, ACCENT 7:1)
    static final int BG = 0xFF17191E, SURFACE = 0xFF22252C, SURFACE_HI = 0xFF2C3039, LINE = 0xFF3A3F4A;
    static final int TEXT = 0xFFE6E7EA, DIM = 0xFFA4A9B4, ACCENT = 0xFF7FB2FF, ON_ACCENT = 0xFF0E1726;
    static final int OK = 0xFF6FD39B, WARN = 0xFFFFC45C, DANGER = 0xFFFF7A7A;

    private static final String METACODEX = "com.pnncl.metacodex";
    private static final String EX = "com.metacodex.extra.";
    private static final int PORT = 47950;
    private static final ArrayDeque<String> RING = new ArrayDeque<>();
    private static final LinkedBlockingQueue<String> OUT = new LinkedBlockingQueue<>(1000);
    private static String tag = "app", caller = "", launchId = "", label = "", version = "";
    private static Thread sender;

    private Mcx() {
    }

    /** Call first in onCreate: the log's name, who launched the app, its label and version. */
    static void start(Activity a) {
        tag = a.getPackageName();
        Intent it = a.getIntent();
        caller = str(it.getStringExtra(EX + "CALLER"));
        launchId = str(it.getStringExtra(EX + "LAUNCH_ID"));
        label = String.valueOf(a.getApplicationInfo().loadLabel(a.getPackageManager()));
        try {
            PackageInfo pi = a.getPackageManager().getPackageInfo(tag, 0);
            version = pi.versionName + " (" + pi.getLongVersionCode() + ")";
        } catch (Exception e) {
            version = "?";
        }
        log("starting %s %s, %s", label, version, fromMetaCodex() ? "launched from MetaCodex" : "launched from outside MetaCodex");
        // a crash: its stack trace into the log (and to MetaCodex) before Android closes the app
        Thread.UncaughtExceptionHandler prev = Thread.getDefaultUncaughtExceptionHandler();
        if (!(prev instanceof CrashLogger)) Thread.setDefaultUncaughtExceptionHandler(new CrashLogger(prev));
    }

    private static final class CrashLogger implements Thread.UncaughtExceptionHandler {
        private final Thread.UncaughtExceptionHandler prev;

        CrashLogger(Thread.UncaughtExceptionHandler prev) {
            this.prev = prev;
        }

        @Override
        public void uncaughtException(Thread t, Throwable e) {
            try {
                log("CRASH in thread %s: %s", t.getName(), e);
                for (StackTraceElement el : e.getStackTrace()) log("    at %s", el);
                for (Throwable c = e.getCause(); c != null; c = c.getCause()) log("  caused by %s", c);
                long until = System.currentTimeMillis() + 800; // the sender thread takes the lines off the queue
                while (!OUT.isEmpty() && System.currentTimeMillis() < until) Thread.sleep(20);
                Thread.sleep(100);
            } catch (Throwable ignored) {
            }
            if (prev != null) prev.uncaughtException(t, e);
        }
    }

    /** The app's name in the Library (android:label). */
    static String label() {
        return label;
    }

    /** True when MetaCodex launched this app (it gave a launch id): Back and Report work then. */
    static boolean fromMetaCodex() {
        return METACODEX.equals(caller) && !launchId.isEmpty();
    }

    /** A log line: logcat (tag = the package) and `mcx-app logs`. Safe from any thread. */
    static void log(String fmt, Object... args) {
        String line = args.length == 0 ? fmt : String.format(Locale.ROOT, fmt, args);
        Log.i(tag, line);
        String stamped = new SimpleDateFormat("HH:mm:ss ", Locale.ROOT).format(new Date()) + line;
        synchronized (RING) {
            RING.addLast(stamped);
            while (RING.size() > 200) RING.removeFirst();
        }
        OUT.offer(line);
        synchronized (Mcx.class) {
            if (sender == null) {
                sender = new Thread(Mcx::send, "mcx-log");
                sender.setDaemon(true);
                sender.start();
            }
        }
    }

    // one connection per run of the app; when MetaCodex does not listen, lines go to logcat only
    private static void send() {
        try (Socket s = new Socket()) {
            s.connect(new InetSocketAddress("127.0.0.1", PORT), 1000);
            OutputStream o = s.getOutputStream();
            o.write(("== " + tag + " connected\n").getBytes(StandardCharsets.UTF_8));
            while (true) {
                String line = OUT.take();
                o.write((line + "\n").getBytes(StandardCharsets.UTF_8));
                o.flush();
            }
        } catch (Exception e) {
            OUT.clear();
        }
    }

    /** Back to MetaCodex: starts it with this launch's id (it shows "Back from <app>"), then closes this app. */
    static void back(Activity a) {
        if (startCaller(a, null)) a.finishAndRemoveTask();
    }

    /** Report to MetaCodex: a picture of this window and the last 60 log lines travel with Back. */
    static void report(Activity a) {
        log("report: picture of the window and the log to MetaCodex");
        if (startCaller(a, picture(a.getWindow().getDecorView()))) a.finishAndRemoveTask();
    }

    private static boolean startCaller(Activity a, byte[] png) {
        Intent it = a.getPackageManager().getLaunchIntentForPackage(caller.isEmpty() ? METACODEX : caller);
        if (it == null) {
            log("back: MetaCodex is not installed");
            return false;
        }
        it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
        it.putExtra(EX + "RETURN_FROM", tag);
        it.putExtra(EX + "LAUNCH_ID", launchId);
        it.putExtra(EX + "APP_LABEL", label);
        it.putExtra(EX + "APP_VERSION", version);
        if (png != null) {
            it.putExtra(EX + "REPORT", "1");
            it.putExtra(EX + "REPORT_LOG", recentLog(60));
            it.putExtra(EX + "REPORT_PNG", png);
        }
        Bundle opts = null;
        if (Build.VERSION.SDK_INT >= 34) opts = ActivityOptions.makeBasic().setShareIdentityEnabled(true).toBundle();
        try {
            a.startActivity(it, opts);
            log("back: MetaCodex started%s", png != null ? " with a report (" + png.length / 1024 + " KB picture)" : "");
            return true;
        } catch (Exception e) {
            log("back: MetaCodex could not be started: %s", e);
            return false;
        }
    }

    private static String recentLog(int n) {
        synchronized (RING) {
            StringBuilder sb = new StringBuilder();
            int skip = Math.max(0, RING.size() - n), i = 0;
            for (String l : RING) if (i++ >= skip) sb.append(l).append('\n');
            return sb.toString();
        }
    }

    // the window as a PNG under about 450 KB (an intent's extras must stay small)
    private static byte[] picture(View v) {
        if (v.getWidth() <= 0 || v.getHeight() <= 0) return new byte[0];
        Bitmap full = Bitmap.createBitmap(v.getWidth(), v.getHeight(), Bitmap.Config.ARGB_8888);
        v.draw(new Canvas(full));
        float scale = 1f;
        while (true) {
            Bitmap b = scale == 1f ? full : Bitmap.createScaledBitmap(full, Math.max(1, (int) (full.getWidth() * scale)), Math.max(1, (int) (full.getHeight() * scale)), true);
            ByteArrayOutputStream o = new ByteArrayOutputStream();
            b.compress(Bitmap.CompressFormat.PNG, 100, o);
            if (o.size() < 450 * 1024 || scale < 0.2f) return o.toByteArray();
            scale *= 0.75f;
        }
    }

    // ---------------------------------------------------------------- the look

    static int dp(Context c, float v) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, c.getResources().getDisplayMetrics()));
    }

    static TextView text(Context c, String s, float sp, int color, boolean bold) {
        TextView t = new TextView(c);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        t.setLineSpacing(0, 1.15f);
        if (bold) t.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        return t;
    }

    /** A button: 60dp high (primary: the accent colour), with pressed, hovered (ray / gaze) and focused states. */
    static Button button(Context c, String s, boolean primary) {
        Button b = new Button(c);
        b.setText(s);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18);
        b.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        b.setTextColor(primary ? ON_ACCENT : TEXT);
        b.setMinHeight(dp(c, 60));
        b.setMinimumHeight(dp(c, 60));
        b.setMinWidth(dp(c, 120));
        b.setPadding(dp(c, 24), 0, dp(c, 24), 0);
        b.setStateListAnimator(null);
        int base = primary ? ACCENT : SURFACE_HI, hover = primary ? 0xFFA3C8FF : 0xFF3A3F4B;
        StateListDrawable states = new StateListDrawable();
        states.addState(new int[]{android.R.attr.state_hovered}, round(c, hover, primary ? 0 : LINE));
        states.addState(new int[]{android.R.attr.state_focused}, round(c, hover, ACCENT));
        states.addState(new int[]{-android.R.attr.state_enabled}, round(c, 0xFF2A2D33, 0));
        states.addState(new int[]{}, round(c, base, primary ? 0 : LINE));
        b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x40FFFFFF), states, null));
        return b;
    }

    static GradientDrawable round(Context c, int fill, int stroke) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(fill);
        g.setCornerRadius(dp(c, 14));
        if (stroke != 0) g.setStroke(dp(c, 1), stroke);
        return g;
    }

    /** The window's top row: the app's name, then REPORT and BACK TO METACODEX when MetaCodex launched it. */
    static LinearLayout topBar(Activity a, String title) {
        LinearLayout bar = new LinearLayout(a);
        bar.setOrientation(LinearLayout.HORIZONTAL);
        bar.setGravity(Gravity.CENTER_VERTICAL);
        TextView t = text(a, title, 26, TEXT, true);
        bar.addView(t, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        if (fromMetaCodex()) {
            Button report = button(a, "Report", false);
            report.setOnClickListener(v -> report(a));
            Button back = button(a, "Back to MetaCodex", false);
            back.setOnClickListener(v -> back(a));
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
            lp.setMarginStart(dp(a, 16));
            bar.addView(report, lp);
            bar.addView(back, new LinearLayout.LayoutParams(lp));
        }
        return bar;
    }

    private static String str(String s) {
        return s == null ? "" : s;
    }
}
