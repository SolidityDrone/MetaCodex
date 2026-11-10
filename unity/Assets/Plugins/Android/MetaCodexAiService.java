package com.pnncl.metacodex.ai;

import android.app.Service;
import android.content.Intent;
import android.os.Binder;
import android.os.IBinder;
import android.util.Log;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

/**
 * Hosts the local AI service (docs/voice.md) from the app's own ":ai" process.
 *
 * Horizon OS keeps the running immersive app, and every process it starts, in the "immersive" CPU set: cores
 * 3-5, the frame loop's cores; a child cannot leave it (taskset to 0-2 fails with EINVAL). A separate Android
 * process is placed by the system instead (the foreground / background sets, cores 0-2), and the child it starts
 * inherits that placement. So QvacHost asks this service to start the Bare runtime, and MqhHost MQH (proot +
 * node): extras "argv", "env", "cwd", "log" and "name" (the child: "qvac" when absent, "mqh"); extra action=stop
 * ends that child. A new start of a name replaces its running child (it brings a new token); the others go on.
 */
public class MetaCodexAiService extends Service {
    private static final String TAG = "MetaCodexAI";
    private static final java.util.Map<String, Process> children = new java.util.HashMap<>();

    /** The app binds too: a bound service is not stopped when Android stops an idle app's started services
     *  (that ended the AI service while the headset slept). */
    @Override
    public IBinder onBind(Intent intent) {
        return new Binder();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) return START_NOT_STICKY;
        String name = intent.getStringExtra("name");
        if (name == null || name.isEmpty()) name = "qvac";
        if ("stop".equals(intent.getStringExtra("action"))) {
            stopChild(name);
            synchronized (children) { if (children.isEmpty()) stopSelf(); }
            return START_NOT_STICKY;
        }
        String[] argv = intent.getStringArrayExtra("argv");
        String[] env = intent.getStringArrayExtra("env");
        String cwd = intent.getStringExtra("cwd");
        String log = intent.getStringExtra("log");
        if (argv == null || argv.length == 0) return START_NOT_STICKY;
        stopChild(name);
        try {
            String allowed = cpusAllowed();
            List<String> cmd = new ArrayList<>();
            if (covers(allowed, 0, 2)) { // cores 0-2 when this process may use them (it may have 0-5)
                cmd.add("/system/bin/taskset");
                cmd.add("7");
            }
            cmd.addAll(Arrays.asList(argv));
            ProcessBuilder pb = new ProcessBuilder(cmd);
            if (cwd != null) pb.directory(new File(cwd));
            Map<String, String> e = pb.environment();
            if (env != null) {
                for (String kv : env) {
                    int i = kv.indexOf('=');
                    if (i > 0) e.put(kv.substring(0, i), kv.substring(i + 1));
                }
            }
            pb.redirectErrorStream(true);
            if (log != null) pb.redirectOutput(ProcessBuilder.Redirect.appendTo(new File(log)));
            Process p = pb.start();
            synchronized (children) { children.put(name, p); }
            Log.i(TAG, "started " + name + " (process cpus " + allowed + ", cpuset " + read("/proc/self/cpuset").trim() + "): " + cmd);
        } catch (Exception ex) {
            Log.e(TAG, "start failed", ex);
        }
        return START_NOT_STICKY;
    }

    @Override
    public void onDestroy() {
        String[] names;
        synchronized (children) { names = children.keySet().toArray(new String[0]); }
        for (String n : names) stopChild(n);
        super.onDestroy();
    }

    private static void stopChild(String name) {
        Process p;
        synchronized (children) { p = children.remove(name); }
        if (p == null) return;
        try {
            p.destroy(); // SIGTERM
            if (!p.waitFor(3, TimeUnit.SECONDS)) p.destroyForcibly().waitFor(2, TimeUnit.SECONDS);
        } catch (Exception ex) {
            Log.w(TAG, "stop: " + ex);
        }
    }

    /** "0-2,4" style list from /proc/self/status. */
    private static String cpusAllowed() {
        for (String line : read("/proc/self/status").split("\n")) {
            if (line.startsWith("Cpus_allowed_list:")) return line.substring(line.indexOf(':') + 1).trim();
        }
        return "";
    }

    private static boolean covers(String list, int lo, int hi) {
        boolean[] on = new boolean[64];
        for (String part : list.split(",")) {
            part = part.trim();
            if (part.isEmpty()) continue;
            try {
                int dash = part.indexOf('-');
                int a = Integer.parseInt(dash < 0 ? part : part.substring(0, dash));
                int b = dash < 0 ? a : Integer.parseInt(part.substring(dash + 1));
                for (int c = a; c <= b && c < 64; c++) on[c] = true;
            } catch (NumberFormatException ignored) {
            }
        }
        for (int c = lo; c <= hi; c++) if (!on[c]) return false;
        return true;
    }

    private static String read(String path) {
        StringBuilder sb = new StringBuilder();
        try (BufferedReader r = new BufferedReader(new FileReader(path))) {
            String l;
            while ((l = r.readLine()) != null) sb.append(l).append('\n');
        } catch (Exception ignored) {
        }
        return sb.toString();
    }
}
