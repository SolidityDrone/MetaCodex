package com.pnncl.metacodex.web;

import android.app.Activity;
import android.graphics.Canvas;
import android.graphics.PixelFormat;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.SystemClock;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.ViewGroup;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;

import org.json.JSONObject;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.concurrent.locks.ReentrantLock;

/**
 * MetaCodex's in-app browser (owner 2026-11-10: the system browser takes the app away; the headset cannot show it
 * beside an immersive app). Android's own WebView (the headset's com.android.webview, Chromium) is laid out off
 * screen in the activity (attached, so it runs, but outside the window, so the window never draws it) and drawn,
 * whenever the page changes and at most about 30 times a second, into an ImageReader through a hardware canvas.
 * Each frame is copied into one direct buffer that the Unity side uploads into a texture (MetaCodex runs on Vulkan:
 * no GL texture to share). Touch, hover, keys and text come from the Unity side, posted to the UI thread.
 * WebView debugging is on: chrome://inspect on the PC (adb) sees the pages.
 */
public final class MetaCodexWebView {
    private final Activity activity;
    private final int width, height;
    private final ByteBuffer pixels;              // RGBA, width * height * 4, read by Unity between beginRead / endRead
    private final ReentrantLock lock = new ReentrantLock();
    private final Handler ui = new Handler(Looper.getMainLooper());
    private HandlerThread thread;
    private Handler copier;
    private FrameLayout host;
    private WebView web;
    private ImageReader reader;
    private long frame, taken, downTime;
    private volatile boolean drawPending, closed, paused;
    private volatile String url = "", title = "", error = "", external = "";
    private volatile String[] externalHosts = new String[0], externalPaths = new String[0];
    private volatile int progress;
    private volatile boolean canBack, canForward, loading;
    private final Runnable draw = this::drawFrame;
    private final Runnable keepAlive = new Runnable() {
        @Override public void run() {
            if (closed) return;
            if (!paused) scheduleDraw();          // catches a change the page did not report
            ui.postDelayed(this, 250);
        }
    };

    public MetaCodexWebView(Activity activity, int width, int height) {
        this.activity = activity;
        this.width = width;
        this.height = height;
        pixels = ByteBuffer.allocateDirect(width * height * 4).order(ByteOrder.nativeOrder());
        thread = new HandlerThread("mcx-webview-copy");
        thread.start();
        copier = new Handler(thread.getLooper());
        ui.post(this::create);
    }

    private void create() {
        try {
            reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2);
            reader.setOnImageAvailableListener(this::onImage, copier);
            WebView.setWebContentsDebuggingEnabled(true);
            web = new WebView(activity) {
                @Override public void invalidate() { super.invalidate(); scheduleDraw(); }
            };
            WebSettings s = web.getSettings();
            s.setJavaScriptEnabled(true);
            s.setDomStorageEnabled(true);
            s.setUseWideViewPort(true);
            s.setLoadWithOverviewMode(true);
            s.setSupportZoom(true);
            s.setBuiltInZoomControls(true);
            s.setDisplayZoomControls(false);
            s.setMediaPlaybackRequiresUserGesture(false);
            s.setMixedContentMode(WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE);
            s.setSupportMultipleWindows(false);   // target=_blank opens here
            s.setOffscreenPreRaster(true);         // it lives off screen: raster its tiles anyway (else a GPU-drawn page can come out blank)
            web.setBackgroundColor(0xFFFFFFFF);
            web.setWebViewClient(new WebViewClient() {
                @Override public boolean shouldOverrideUrlLoading(WebView v, WebResourceRequest r) {
                    // a sign-in page never loads here: Unity opens it in Horizon OS's browser (see setExternal)
                    String u = String.valueOf(r.getUrl());
                    if (r.isForMainFrame() && isExternal(u)) { external = u; return true; }
                    return false;
                }
                @Override public void onPageStarted(WebView v, String u, android.graphics.Bitmap icon) { url = u; loading = true; error = ""; nav(); scheduleDraw(); }
                @Override public void onPageFinished(WebView v, String u) { url = u; loading = false; nav(); scheduleDraw(); }
                @Override public void onReceivedError(WebView v, WebResourceRequest r, WebResourceError e) {
                    if (r.isForMainFrame()) error = String.valueOf(e.getDescription());
                }
            });
            web.setWebChromeClient(new WebChromeClient() {
                @Override public void onProgressChanged(WebView v, int p) { progress = p; }
                @Override public void onReceivedTitle(WebView v, String t) { title = t == null ? "" : t; }
            });
            host = new FrameLayout(activity);
            host.addView(web, new FrameLayout.LayoutParams(width, height));
            host.setTranslationX(-4f * width);     // laid out and running, but outside the window: it never draws there
            ((ViewGroup) activity.getWindow().getDecorView()).addView(host, new FrameLayout.LayoutParams(width, height));
            ui.postDelayed(keepAlive, 250);
        } catch (Throwable t) {
            error = "webview: " + t;
        }
    }

    private void nav() {
        if (web == null) return;
        canBack = web.canGoBack();
        canForward = web.canGoForward();
    }

    private void scheduleDraw() {
        if (drawPending || closed || paused) return;
        drawPending = true;
        ui.postDelayed(draw, 33);
    }

    // on the UI thread: the page into the ImageReader through a hardware canvas
    private void drawFrame() {
        drawPending = false;
        if (closed || paused || web == null || reader == null) return;
        Surface surface = reader.getSurface();
        Canvas c = null;
        try {
            c = surface.lockHardwareCanvas();
            c.drawColor(0xFFFFFFFF);
            web.draw(c);
        } catch (Throwable t) {
            error = "draw: " + t;
        } finally {
            if (c != null) try { surface.unlockCanvasAndPost(c); } catch (Throwable ignored) { }
        }
    }

    // on the copy thread: the newest image into the shared buffer, rows packed
    private void onImage(ImageReader r) {
        Image img = null;
        try {
            img = r.acquireLatestImage();
            if (img == null) return;
            Image.Plane plane = img.getPlanes()[0];
            ByteBuffer src = plane.getBuffer();
            int stride = plane.getRowStride(), row = width * 4;
            lock.lock();
            try {
                pixels.clear();
                if (stride == row) {
                    ByteBuffer all = src.duplicate();
                    all.position(0);
                    all.limit(row * height);
                    pixels.put(all);
                } else {
                    for (int y = 0; y < height; y++) {
                        ByteBuffer line = src.duplicate();
                        line.position(y * stride);
                        line.limit(y * stride + row);
                        pixels.put(line);
                    }
                }
                frame++;
            } finally {
                lock.unlock();
            }
        } catch (Throwable t) {
            error = "copy: " + t;
        } finally {
            if (img != null) img.close();
        }
    }

    // ---- the Unity side (main thread)

    public ByteBuffer pixels() { return pixels; }
    public int width() { return width; }
    public int height() { return height; }

    /** Locks the buffer; true when it holds a frame the Unity side has not taken yet. Always call endRead after.
     *  Never waits: while the copy thread is writing a frame this returns false (Unity takes it next frame). */
    public boolean beginRead() {
        if (!lock.tryLock()) return false;
        boolean fresh = frame != taken;
        taken = frame;
        return fresh;
    }

    public void endRead() { if (lock.isHeldByCurrentThread()) lock.unlock(); }

    /** url, title, progress, can go back, can go forward, loading, the last error: tab separated. */
    public String state() {
        String ext = external;
        external = "";
        return url + "\t" + title + "\t" + progress + "\t" + (canBack ? 1 : 0) + "\t" + (canForward ? 1 : 0) + "\t" + (loading ? 1 : 0) + "\t" + error + "\t" + ext;
    }

    /** The pages that must not load in this WebView (sign-in pages: their session would stay in the app's cookies):
     *  hosts (a host or any of its subdomains) and path parts, one per line. A link to one is not followed; state()
     *  reports it once as its last field and Unity opens it in the system browser. */
    public void setExternal(String hosts, String paths) {
        externalHosts = hosts == null || hosts.isEmpty() ? new String[0] : hosts.split("\n");
        externalPaths = paths == null || paths.isEmpty() ? new String[0] : paths.split("\n");
    }

    private boolean isExternal(String u) {
        android.net.Uri uri = android.net.Uri.parse(u);
        String host = uri.getHost() == null ? "" : uri.getHost().toLowerCase();
        String path = uri.getPath() == null ? "" : uri.getPath().toLowerCase();
        if (host.isEmpty() || host.equals("localhost") || host.startsWith("127.")) return false;
        for (String h : externalHosts) if (host.equals(h) || host.endsWith("." + h)) return true;
        for (String p : externalPaths) if (path.contains(p)) return true;
        return false;
    }

    public void load(final String u) { ui.post(() -> { if (web != null) { error = ""; web.loadUrl(u); } }); }
    public void loadHtml(final String html, final String baseUrl) { ui.post(() -> { if (web != null) web.loadDataWithBaseURL(baseUrl, html, "text/html", "utf-8", null); }); }
    public void back() { ui.post(() -> { if (web != null && web.canGoBack()) web.goBack(); }); }
    public void forward() { ui.post(() -> { if (web != null && web.canGoForward()) web.goForward(); }); }
    public void reload() { ui.post(() -> { if (web != null) web.reload(); }); }

    /** A touch in page pixels: 0 down, 1 up, 2 move (MotionEvent's actions). */
    public void touch(final int action, final float x, final float y) {
        ui.post(() -> {
            if (web == null) return;
            long now = SystemClock.uptimeMillis();
            if (action == MotionEvent.ACTION_DOWN) downTime = now;
            MotionEvent ev = MotionEvent.obtain(downTime, now, action, x, y, 0);
            ev.setSource(InputDevice.SOURCE_TOUCHSCREEN);
            web.dispatchTouchEvent(ev);
            ev.recycle();
            scheduleDraw();
        });
    }

    /** The pointer over the page without pressing (hover styles, menus that open on hover). */
    public void hover(final float x, final float y) {
        ui.post(() -> {
            if (web == null) return;
            long now = SystemClock.uptimeMillis();
            MotionEvent ev = MotionEvent.obtain(now, now, MotionEvent.ACTION_HOVER_MOVE, x, y, 0);
            ev.setSource(InputDevice.SOURCE_MOUSE);
            web.dispatchGenericMotionEvent(ev);
            ev.recycle();
        });
    }

    /** The wheel: positive dy scrolls the page down. */
    public void wheel(final float x, final float y, final float dy) {
        ui.post(() -> {
            if (web == null) return;
            long now = SystemClock.uptimeMillis();
            MotionEvent.PointerProperties[] props = { new MotionEvent.PointerProperties() };
            props[0].id = 0;
            props[0].toolType = MotionEvent.TOOL_TYPE_MOUSE;
            MotionEvent.PointerCoords[] coords = { new MotionEvent.PointerCoords() };
            coords[0].x = x;
            coords[0].y = y;
            coords[0].setAxisValue(MotionEvent.AXIS_VSCROLL, -dy);
            MotionEvent ev = MotionEvent.obtain(now, now, MotionEvent.ACTION_SCROLL, 1, props, coords, 0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_MOUSE, 0);
            web.dispatchGenericMotionEvent(ev);
            ev.recycle();
            scheduleDraw();
        });
    }

    /** Text into the focused field of the page. */
    public void text(final String s) {
        ui.post(() -> { if (web != null) web.evaluateJavascript("document.execCommand('insertText', false, " + JSONObject.quote(s) + ");", null); });
    }

    /** A key (KeyEvent codes: 66 enter, 67 backspace, 61 tab, 111 escape, 19-22 arrows). */
    public void key(final int code) {
        ui.post(() -> {
            if (web == null) return;
            long now = SystemClock.uptimeMillis();
            web.dispatchKeyEvent(new KeyEvent(now, now, KeyEvent.ACTION_DOWN, code, 0));
            web.dispatchKeyEvent(new KeyEvent(now, now, KeyEvent.ACTION_UP, code, 0));
            scheduleDraw();
        });
    }

    /** The window closed: the page stops (timers, video); open again: it goes on. */
    public void setPaused(final boolean p) {
        paused = p;
        ui.post(() -> {
            if (web == null) return;
            if (p) { web.onPause(); } else { web.onResume(); scheduleDraw(); }
        });
    }

    public void close() {
        closed = true;
        ui.post(() -> {
            try {
                if (host != null && host.getParent() instanceof ViewGroup) ((ViewGroup) host.getParent()).removeView(host);
                if (web != null) { web.stopLoading(); web.destroy(); }
                if (reader != null) reader.close();
            } catch (Throwable ignored) { }
            web = null;
            reader = null;
        });
        if (thread != null) thread.quitSafely();
    }
}
