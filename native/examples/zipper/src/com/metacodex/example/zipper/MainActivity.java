package com.metacodex.example.zipper;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.provider.DocumentsContract;
import android.provider.OpenableColumns;
import android.text.TextUtils;
import android.text.format.Formatter;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;

import java.io.BufferedOutputStream;
import java.io.FilterOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InterruptedIOException;
import java.io.OutputStream;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.zip.Deflater;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * Zipper: pick files with the system file picker, zip them into one file you choose, see the progress and the result
 * size. Files come and go through the Storage Access Framework (ACTION_OPEN_DOCUMENT, ACTION_CREATE_DOCUMENT): the user
 * picks each file, so the app needs no storage permission. The zipping runs on a worker thread (java.util.zip); the
 * window stays responsive and can be resized meanwhile.
 */
public final class MainActivity extends Activity {
    private static final int PICK = 1, SAVE = 2;
    // already compressed: deflating them again costs time for nothing, so they go in at the fastest level
    private static final Set<String> PACKED = new HashSet<>(Arrays.asList("zip", "gz", "tgz", "xz", "bz2", "7z", "rar",
            "jpg", "jpeg", "png", "gif", "webp", "heic", "avif", "mp4", "mkv", "webm", "mov", "mp3", "m4a", "aac", "ogg",
            "opus", "flac", "apk", "jar", "aar", "docx", "xlsx", "pptx", "glb"));

    private static final class Item {
        final Uri uri;
        final String name;
        final long size; // -1: the provider did not say

        Item(Uri uri, String name, long size) {
            this.uri = uri;
            this.name = name;
            this.size = size;
        }
    }

    private final ArrayList<Item> items = new ArrayList<>();
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());
    private volatile boolean stopAsked;
    private boolean busy;
    private LinearLayout list;
    private TextView heading, empty, status, result;
    private ProgressBar bar;
    private Button add, clear, zip, stop;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        Mcx.start(this);
        setContentView(build());
        refresh();
        status("Add the files to put into one zip file.", Mcx.DIM);
    }

    @Override
    protected void onDestroy() {
        stopAsked = true;
        worker.shutdownNow();
        super.onDestroy();
    }

    // ---------------------------------------------------------------- the window

    private View build() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        int pad = Mcx.dp(this, 32);
        page.setPadding(pad, Mcx.dp(this, 24), pad, pad);
        page.addView(Mcx.topBar(this, Mcx.label()));
        page.addView(Mcx.text(this, "Pick files, then zip them into one file you choose. Everything stays on this headset.",
                18, Mcx.DIM, false), margins(0, 12, 0, 20));

        LinearLayout actions = row();
        add = Mcx.button(this, "Add files…", true);
        add.setOnClickListener(v -> pick());
        clear = Mcx.button(this, "Clear", false);
        clear.setOnClickListener(v -> {
            items.clear();
            result.setText("");
            Mcx.log("cleared the list");
            refresh();
        });
        actions.addView(add);
        actions.addView(clear, margins(16, 0, 0, 0));
        page.addView(actions);

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackground(Mcx.round(this, Mcx.SURFACE, Mcx.LINE));
        int cp = Mcx.dp(this, 20);
        card.setPadding(cp, cp, cp, cp);
        heading = Mcx.text(this, "", 20, Mcx.TEXT, true);
        card.addView(heading);
        empty = Mcx.text(this, "No files yet: Add files… opens the file picker. Pick several at once with a long press.", 18, Mcx.DIM, false);
        card.addView(empty, margins(0, 12, 0, 0));
        list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        card.addView(list, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        page.addView(card, fill(0, 20, 0, 20));

        LinearLayout go = row();
        zip = Mcx.button(this, "Zip to…", true);
        zip.setOnClickListener(v -> save());
        stop = Mcx.button(this, "Stop", false);
        stop.setOnClickListener(v -> {
            stopAsked = true;
            status("Stopping…", Mcx.WARN);
        });
        go.addView(zip);
        go.addView(stop, margins(16, 0, 0, 0));
        page.addView(go);

        bar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        bar.setMax(1000);
        bar.setProgressTintList(android.content.res.ColorStateList.valueOf(Mcx.ACCENT));
        bar.setProgressBackgroundTintList(android.content.res.ColorStateList.valueOf(Mcx.LINE));
        page.addView(bar, fill(0, 20, 0, 8));
        status = Mcx.text(this, "", 18, Mcx.DIM, false);
        page.addView(status);
        result = Mcx.text(this, "", 20, Mcx.OK, true);
        page.addView(result, margins(0, 12, 0, 0));

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(Mcx.BG);
        scroll.addView(page);
        return scroll;
    }

    /** The list, the totals and which buttons work, from `items` and `busy`. */
    private void refresh() {
        list.removeAllViews();
        long total = 0;
        boolean unknown = false;
        for (Item it : items) {
            if (it.size < 0) unknown = true;
            else total += it.size;
            LinearLayout r = row();
            r.setMinimumHeight(Mcx.dp(this, 64));
            TextView name = Mcx.text(this, it.name, 18, Mcx.TEXT, false);
            name.setSingleLine(true);
            name.setEllipsize(TextUtils.TruncateAt.MIDDLE);
            r.addView(name, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
            r.addView(Mcx.text(this, it.size < 0 ? "size ?" : size(it.size), 16, Mcx.DIM, false), margins(16, 0, 16, 0));
            Button remove = Mcx.button(this, "Remove", false);
            remove.setEnabled(!busy);
            remove.setOnClickListener(v -> {
                items.remove(it);
                Mcx.log("removed %s", it.name);
                refresh();
            });
            r.addView(remove);
            list.addView(r, fill(0, 8, 0, 0));
        }
        heading.setText(items.isEmpty() ? "Files" : items.size() + (items.size() == 1 ? " file, " : " files, ")
                + size(total) + (unknown ? " and more" : ""));
        empty.setVisibility(items.isEmpty() ? View.VISIBLE : View.GONE);
        add.setEnabled(!busy);
        clear.setEnabled(!busy && !items.isEmpty());
        zip.setEnabled(!busy && !items.isEmpty());
        stop.setVisibility(busy ? View.VISIBLE : View.GONE);
        bar.setVisibility(busy ? View.VISIBLE : View.INVISIBLE);
    }

    // ---------------------------------------------------------------- picking and saving (Storage Access Framework)

    private void pick() {
        Intent it = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
                .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        launch(it, PICK);
    }

    private void save() {
        String name = items.size() == 1 ? base(items.get(0).name) + ".zip"
                : "files-" + new SimpleDateFormat("yyyy-MM-dd-HHmm", Locale.ROOT).format(new Date()) + ".zip";
        Intent it = new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/zip")
                .putExtra(Intent.EXTRA_TITLE, name);
        launch(it, SAVE);
    }

    private void launch(Intent it, int code) {
        try {
            startActivityForResult(it, code);
        } catch (ActivityNotFoundException e) {
            Mcx.log("no file picker: %s", e);
            status("This headset has no file picker for this request.", Mcx.DANGER);
        }
    }

    @Override
    protected void onActivityResult(int code, int res, Intent data) {
        super.onActivityResult(code, res, data);
        if (res != RESULT_OK || data == null) {
            status(code == PICK ? "No files picked." : "Not saved: no file chosen.", Mcx.DIM);
            return;
        }
        if (code == PICK) {
            List<Uri> uris = new ArrayList<>();
            ClipData clip = data.getClipData();
            if (clip != null) for (int i = 0; i < clip.getItemCount(); i++) uris.add(clip.getItemAt(i).getUri());
            else if (data.getData() != null) uris.add(data.getData());
            int added = 0;
            for (Uri u : uris) {
                boolean have = false;
                for (Item it : items) have |= it.uri.equals(u);
                if (have) continue;
                items.add(describe(u));
                added++;
            }
            Mcx.log("picked %d file(s), %d new", uris.size(), added);
            result.setText("");
            refresh();
            status(added == 0 ? "Those files are in the list already." : "Added " + added + (added == 1 ? " file." : " files."), Mcx.DIM);
        } else if (code == SAVE && data.getData() != null) {
            zip(data.getData());
        }
    }

    /** Its name and size from the provider (OpenableColumns). */
    private Item describe(Uri u) {
        String name = null;
        long size = -1;
        try (Cursor c = getContentResolver().query(u, new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE}, null, null, null)) {
            if (c != null && c.moveToFirst()) {
                if (!c.isNull(0)) name = c.getString(0);
                if (!c.isNull(1)) size = c.getLong(1);
            }
        } catch (Exception e) {
            Mcx.log("no name for %s: %s", u, e);
        }
        if (name == null || name.isEmpty()) name = u.getLastPathSegment() == null ? "file" : u.getLastPathSegment();
        return new Item(u, name, size);
    }

    // ---------------------------------------------------------------- zipping (a worker thread)

    private void zip(Uri dest) {
        final List<Item> todo = new ArrayList<>(items);
        long known = 0;
        for (Item it : todo) known += Math.max(0, it.size);
        final long total = known;
        busy = true;
        stopAsked = false;
        result.setText("");
        refresh();
        bar.setProgress(0);
        status("Zipping…", Mcx.TEXT);
        Mcx.log("zipping %d file(s), %d bytes, into %s", todo.size(), total, dest.getLastPathSegment());
        worker.execute(() -> {
            long t0 = SystemClock.elapsedRealtime();
            long[] last = {0};
            long done = 0, in = 0;
            Counting counted = null;
            try (OutputStream raw = getContentResolver().openOutputStream(dest, "wt")) {
                if (raw == null) throw new IOException("the chosen file cannot be written");
                counted = new Counting(new BufferedOutputStream(raw, 1 << 16));
                ZipOutputStream z = new ZipOutputStream(counted);
                try {
                    Set<String> names = new HashSet<>();
                    byte[] buf = new byte[1 << 16];
                    for (int i = 0; i < todo.size(); i++) {
                        Item it = todo.get(i);
                        z.setLevel(PACKED.contains(ext(it.name)) ? Deflater.BEST_SPEED : Deflater.DEFAULT_COMPRESSION);
                        z.putNextEntry(new ZipEntry(unique(names, it.name)));
                        try (InputStream src = getContentResolver().openInputStream(it.uri)) {
                            if (src == null) throw new IOException(it.name + " cannot be read");
                            int n;
                            while ((n = src.read(buf)) > 0) {
                                if (stopAsked) throw new InterruptedIOException("stopped");
                                z.write(buf, 0, n);
                                done += n;
                                in += n;
                                long now = SystemClock.elapsedRealtime();
                                if (now - last[0] >= 100) { // at most ten updates a second
                                    last[0] = now;
                                    progress(i, todo.size(), it.name, done, total);
                                }
                            }
                        }
                        z.closeEntry();
                        progress(i + 1, todo.size(), it.name, done, total);
                    }
                    z.finish();
                    z.flush();
                } finally {
                    try {
                        z.close(); // releases the deflater; after finish() it writes nothing more
                    } catch (IOException ignored) {
                    }
                }
                final long size = counted.count, before = in, ms = SystemClock.elapsedRealtime() - t0;
                final String saved = name(dest);
                Mcx.log("done: %d file(s), %d -> %d bytes in %d ms", todo.size(), before, size, ms);
                ui.post(() -> finished(true, "Saved " + saved + ": " + size(size) + " from " + size(before)
                        + " (" + todo.size() + (todo.size() == 1 ? " file" : " files") + ") in " + String.format(Locale.ROOT, "%.1f s", ms / 1000.0)));
            } catch (Exception e) {
                boolean stopped = e instanceof InterruptedIOException;
                Mcx.log("%s: %s", stopped ? "stopped" : "failed", e);
                boolean deleted = false;
                try { // a half-written zip is of no use: remove it
                    deleted = DocumentsContract.deleteDocument(getContentResolver(), dest);
                } catch (Exception ignored) {
                }
                String tail = deleted ? " The unfinished file was deleted." : "";
                ui.post(() -> finished(false, (stopped ? "Stopped." : "Could not zip: " + e.getMessage() + ".") + tail));
            }
        });
    }

    private void progress(int i, int n, String name, long done, long total) {
        ui.post(() -> {
            if (!busy) return;
            bar.setProgress(total > 0 ? (int) Math.min(1000, done * 1000 / total) : i * 1000 / Math.max(1, n));
            status("Zipping " + Math.min(i + 1, n) + " of " + n + ": " + name + "  (" + size(done) + (total > 0 ? " of " + size(total) : "") + ")", Mcx.TEXT);
        });
    }

    private void finished(boolean ok, String text) {
        busy = false;
        refresh();
        if (ok) {
            bar.setProgress(1000);
            result.setTextColor(Mcx.OK);
            result.setText(text);
            status("Done.", Mcx.DIM);
        } else {
            result.setTextColor(stopAsked ? Mcx.WARN : Mcx.DANGER);
            result.setText(text);
            status("", Mcx.DIM);
        }
    }

    /** Counts the bytes of the zip as they are written: its size when done. */
    private static final class Counting extends FilterOutputStream {
        long count;

        Counting(OutputStream out) {
            super(out);
        }

        @Override
        public void write(int b) throws IOException {
            out.write(b);
            count++;
        }

        @Override
        public void write(byte[] b, int off, int len) throws IOException {
            out.write(b, off, len);
            count += len;
        }
    }

    // ---------------------------------------------------------------- small helpers

    private static String unique(Set<String> names, String name) {
        String clean = name.replace('/', '_').replace('\\', '_');
        String b = base(clean), e = ext(clean).isEmpty() ? "" : clean.substring(b.length());
        String n = clean;
        for (int k = 2; !names.add(n); k++) n = b + " (" + k + ")" + e;
        return n;
    }

    private static String base(String name) {
        int dot = name.lastIndexOf('.');
        return dot > 0 ? name.substring(0, dot) : name;
    }

    private static String ext(String name) {
        int dot = name.lastIndexOf('.');
        return dot > 0 ? name.substring(dot + 1).toLowerCase(Locale.ROOT) : "";
    }

    private String name(Uri u) {
        return describe(u).name;
    }

    private String size(long bytes) {
        return Formatter.formatFileSize(this, bytes);
    }

    private void status(String s, int color) {
        status.setTextColor(color);
        status.setText(s);
    }

    private LinearLayout row() {
        LinearLayout r = new LinearLayout(this);
        r.setOrientation(LinearLayout.HORIZONTAL);
        r.setGravity(Gravity.CENTER_VERTICAL);
        return r;
    }

    private LinearLayout.LayoutParams margins(int l, int t, int r, int b) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.setMargins(Mcx.dp(this, l), Mcx.dp(this, t), Mcx.dp(this, r), Mcx.dp(this, b));
        return lp;
    }

    private LinearLayout.LayoutParams fill(int l, int t, int r, int b) {
        LinearLayout.LayoutParams lp = margins(l, t, r, b);
        lp.width = LinearLayout.LayoutParams.MATCH_PARENT;
        return lp;
    }
}
