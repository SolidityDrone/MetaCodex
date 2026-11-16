package com.pnncl.metacodex.web;

import android.app.Activity;
import android.app.Application;
import android.app.Presentation;
import android.content.Context;
import android.content.Intent;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.graphics.drawable.ColorDrawable;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.net.Uri;
import android.net.http.SslError;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Log;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.webkit.GeolocationPermissions;
import android.webkit.JsPromptResult;
import android.webkit.JsResult;
import android.webkit.PermissionRequest;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.SslErrorHandler;
import android.webkit.URLUtil;
import android.webkit.ValueCallback;
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
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import java.util.concurrent.locks.ReentrantLock;

/**
 * MetaCodex's in-app browser (owner 2026-11-10: the system browser takes the app away; the headset cannot show it
 * beside an immersive app). Android's own WebView (the headset's com.android.webview, Chromium) renders the page; the
 * Unity side uploads its frames into a texture (MetaCodex runs on Vulkan: no GL texture to share).
 *
 * Capture (2026-11-16, owner: "videos that are not on YouTube play only a few frames"): the WebView is the content of
 * a Presentation on a private VirtualDisplay whose surface is an ImageReader. Its window is hardware accelerated
 * (Unity's activity is not: android:hardwareAccelerated="false", and inline HTML5 video needs hardware acceleration),
 * on screen for Chromium (so it renders, plays and animates at the display's rate) and never focusable (the app keeps
 * the input focus). The system composes each new frame of that display into the ImageReader; a copy thread packs the
 * newest image into one direct buffer, at most 60 times a second and only when a new image arrived (an idle page costs
 * nothing). If the VirtualDisplay or the Presentation cannot be made, or no frame comes from it, the old path takes
 * over: the WebView off screen in the activity, drawn through a hardware canvas into the ImageReader when it changes.
 *
 * Touch, hover, wheel, keys and text come from the Unity side, posted to the UI thread and dispatched to the page's
 * views (page pixels, top-left origin). A page's full-screen view (video) fills the same surface. Page dialogs, notices
 * (downloads, refused permissions, file pickers, links of other apps) and errors are reported through state(); camera,
 * microphone and location requests are refused. WebView debugging is on: chrome://inspect on the PC (adb) sees the page.
 */
public final class MetaCodexWebView {
    private static final String TAG = "MetaCodexWeb";
    /** Pauses the page's own audio and video (the top document; a closed window or a paused app must be silent). */
    private static final String PAUSE_MEDIA = "(function(){var m=document.querySelectorAll('video,audio');for(var i=0;i<m.length;i++){try{m[i].pause();}catch(e){}}})();";

    private final Activity activity;
    private final int width, height;
    private final ByteBuffer pixels;              // RGBA, width * height * 4, read by Unity between poll and endRead
    private final ReentrantLock lock = new ReentrantLock();
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final HandlerThread thread;
    private final Handler copier;
    private final AtomicInteger stateVersion = new AtomicInteger(1);
    private final AtomicReference<String> external = new AtomicReference<>(""), notice = new AtomicReference<>("");

    // UI thread
    private FrameLayout host;                      // the page, and a page's full-screen view over it
    private WebView web;
    private View custom;                           // the page's full-screen view (inside `customFrame`)
    private FrameLayout customFrame;
    private WebChromeClient.CustomViewCallback customCallback;
    private VirtualDisplay display;
    private Presentation presentation;
    private boolean canvasMode, appliedPause, drawPending;
    private long downTime;
    private JsResult js;
    private int jsSeq;
    private String mainUrl = "";                   // the page being loaded in the main frame (its SSL errors are the page's)
    private Application.ActivityLifecycleCallbacks life;

    // copy thread
    private volatile ImageReader reader;
    private long lastCopy;
    private boolean drainPosted;
    private volatile int minCopyMs = 16;           // at most 60 copies a second
    private volatile long imagesSeen;

    // shared
    private volatile long frame;
    private long taken;                            // Unity's thread
    private int stateSeen;                         // Unity's thread
    private volatile boolean closed, paused, activityPaused;
    private volatile String url = "", title = "", error = "", errorUrl = "", mode = "", jsKind = "", jsMessage = "";
    private volatile String[] externalHosts = new String[0], externalPaths = new String[0];
    private volatile int progress, jsId;
    private volatile boolean canBack, canForward, loading, fullscreen;

    private final Runnable draw = this::drawFrame;
    private final Runnable drain = this::drainLatest;
    private final Runnable keepAlive = new Runnable() {
        @Override public void run() {
            if (closed || !canvasMode) return;
            if (!paused && !activityPaused) scheduleDraw();          // catches a change the page did not report
            ui.postDelayed(this, custom != null ? 33 : 250);          // a full-screen video draws on its own
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

    // ---- setup (UI thread)

    private void create() {
        try {
            WebView.setWebContentsDebuggingEnabled(true);
            reader = newReader();
            web = makeWeb();
            host = new FrameLayout(activity);
            host.setBackgroundColor(Color.WHITE);
            host.addView(web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            if (!showOnDisplay()) showOffScreen("the virtual display could not be made");
            life = new Application.ActivityLifecycleCallbacks() {
                @Override public void onActivityResumed(Activity a) { if (a == activity) { activityPaused = false; applyPause(); } }
                @Override public void onActivityPaused(Activity a) { if (a == activity) { activityPaused = true; applyPause(); } }
                @Override public void onActivityCreated(Activity a, Bundle b) { }
                @Override public void onActivityStarted(Activity a) { }
                @Override public void onActivityStopped(Activity a) { }
                @Override public void onActivitySaveInstanceState(Activity a, Bundle b) { }
                @Override public void onActivityDestroyed(Activity a) { }
            };
            activity.getApplication().registerActivityLifecycleCallbacks(life);
        } catch (Throwable t) {
            Log.e(TAG, "web: the WebView could not be made", t);
            error = "webview: " + t;
        }
        bump();
    }

    private ImageReader newReader() {
        ImageReader r = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 3);
        r.setOnImageAvailableListener(this::onImage, copier);
        return r;
    }

    private WebView makeWeb() {
        // the activity's context: the page's dialogs and pickers need an activity, the page's scale is the app's
        WebView w = new WebView(activity) {
            @Override public void invalidate() { super.invalidate(); if (canvasMode) scheduleDraw(); }
        };
        WebSettings s = w.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setUseWideViewPort(true);
        s.setLoadWithOverviewMode(true);
        s.setSupportZoom(true);
        s.setBuiltInZoomControls(true);
        s.setDisplayZoomControls(false);
        s.setMixedContentMode(WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE);
        s.setSupportMultipleWindows(false);            // target=_blank and window.open load in this window
        s.setJavaScriptCanOpenWindowsAutomatically(false);
        s.setGeolocationEnabled(false);
        // the media rule stays the browsers' one: sound starts after a touch on the page (touches are real input events)
        String ua = s.getUserAgentString();
        if (ua != null && ua.contains("; wv)")) s.setUserAgentString(ua.replace("; wv)", ")")); // sites send the full player, not "open in the app"
        w.setBackgroundColor(Color.WHITE);
        w.setWebViewClient(new Client());
        w.setWebChromeClient(new Chrome());
        w.setDownloadListener((u, agent, disposition, mime, length) -> {
            say("Downloads are not saved here (" + URLUtil.guessFileName(u, disposition, mime) + "): \u21d7 opens the page in the system browser");
        });
        return w;
    }

    /** The WebView as the content of a Presentation on a private VirtualDisplay that renders into the ImageReader. */
    private boolean showOnDisplay() {
        VirtualDisplay vd = null;
        Presentation p = null;
        try {
            DisplayManager dm = (DisplayManager) activity.getSystemService(Context.DISPLAY_SERVICE);
            int dpi = activity.getResources().getDisplayMetrics().densityDpi; // the page lays out as it did in the activity
            vd = dm.createVirtualDisplay("metacodex-web", width, height, dpi, reader.getSurface(), DisplayManager.VIRTUAL_DISPLAY_FLAG_OWN_CONTENT_ONLY);
            if (vd == null) { Log.w(TAG, "web: createVirtualDisplay returned nothing"); return false; }
            p = new Presentation(activity, vd.getDisplay()) {
                @Override protected void onCreate(Bundle b) {
                    super.onCreate(b);
                    Window w = getWindow();
                    // before the content: hardware accelerated (video, WebGL, GPU raster); never focusable: the app's
                    // window keeps the input focus, the system keyboard never opens for this window
                    w.setFlags(WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED | WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
                            WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED | WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE);
                    w.setBackgroundDrawable(new ColorDrawable(Color.WHITE));
                    setContentView(host, new ViewGroup.LayoutParams(width, height));
                }
            };
            p.setCancelable(false);
            p.show();
            display = vd;
            presentation = p;
            canvasMode = false;
            mode = "display";
            web.requestFocus();                        // the page has the focus inside its own window (fields, keys)
            Log.i(TAG, "web: capture=virtual-display " + width + "x" + height + " dpi " + dpi);
            ui.postDelayed(this::checkDisplayFrames, 3000);
            return true;
        } catch (Throwable t) {
            Log.w(TAG, "web: virtual display failed: " + t);
            try { if (p != null) p.dismiss(); } catch (Throwable ignored) { }
            try { if (vd != null) vd.release(); } catch (Throwable ignored) { }
            if (host.getParent() instanceof ViewGroup) ((ViewGroup) host.getParent()).removeView(host);
            return false;
        }
    }

    /** No image from the virtual display 3 s after it showed (it always draws once): the canvas path instead. */
    private void checkDisplayFrames() {
        if (closed || canvasMode || display == null) return;
        if (paused || activityPaused) { ui.postDelayed(this::checkDisplayFrames, 1000); return; }
        if (imagesSeen > 0) return;
        Log.w(TAG, "web: no frame from the virtual display in 3 s");
        try { if (presentation != null) presentation.dismiss(); } catch (Throwable ignored) { }
        try { display.release(); } catch (Throwable ignored) { }
        presentation = null;
        display = null;
        if (host.getParent() instanceof ViewGroup) ((ViewGroup) host.getParent()).removeView(host);
        ImageReader old = reader;
        reader = newReader();                          // a fresh surface: the display was its producer
        if (old != null) copier.post(old::close);
        showOffScreen("no frame from the virtual display");
    }

    /** The fallback: laid out and running in the activity but outside its window, drawn into the ImageReader. */
    private void showOffScreen(String why) {
        canvasMode = true;
        mode = "canvas";
        web.getSettings().setOffscreenPreRaster(true); // off screen: raster its tiles anyway (else a GPU-drawn page can come out blank)
        host.setTranslationX(-4f * width);             // laid out and running, but outside the window: it never draws there
        ((ViewGroup) activity.getWindow().getDecorView()).addView(host, new FrameLayout.LayoutParams(width, height));
        Log.w(TAG, "web: capture=canvas (fallback: " + why + "); video may stop after a few frames on this path");
        ui.postDelayed(keepAlive, 250);
        scheduleDraw();
        bump();
    }

    private void bump() { stateVersion.incrementAndGet(); }

    private void say(String s) { notice.set(s); bump(); }

    private void nav() {
        if (web == null) return;
        canBack = web.canGoBack();
        canForward = web.canGoForward();
    }

    private static String clean(String s) { return s == null ? "" : s.replace('\t', ' '); }

    /** What went wrong, in plain words, then the engine's own code. */
    private static String plain(int code, String desc) {
        String d = desc == null ? "" : desc;
        String w;
        if (d.contains("CLEARTEXT_NOT_PERMITTED")) w = "Plain http pages are blocked by the app's network settings: try https";
        else if (d.contains("INTERNET_DISCONNECTED")) w = "No network connection";
        else switch (code) {
            case WebViewClient.ERROR_HOST_LOOKUP: w = "The address was not found"; break;
            case WebViewClient.ERROR_CONNECT: w = "The server did not answer (is it running?)"; break;
            case WebViewClient.ERROR_TIMEOUT: w = "The page took too long to answer"; break;
            case WebViewClient.ERROR_FAILED_SSL_HANDSHAKE: w = "A secure connection could not be made"; break;
            case WebViewClient.ERROR_BAD_URL: w = "The address is not valid"; break;
            case WebViewClient.ERROR_UNSUPPORTED_SCHEME: w = "This kind of link cannot open here"; break;
            case WebViewClient.ERROR_TOO_MANY_REQUESTS: w = "The site refused: too many requests"; break;
            default: w = "The page did not open";
        }
        return d.isEmpty() ? w : w + " (" + clean(d) + ")";
    }

    private final class Client extends WebViewClient {
        @Override public boolean shouldOverrideUrlLoading(WebView v, WebResourceRequest r) {
            Uri uri = r.getUrl();
            String u = String.valueOf(uri);
            String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase();
            boolean main = r.isForMainFrame();
            // a sign-in page never loads here: Unity opens it in the system browser (see setExternal)
            if (main && isExternal(u)) { external.set(u); bump(); return true; }
            if (scheme.equals("intent")) {               // another app's link: its web address when it has one
                String fallback = null;
                try { fallback = Intent.parseUri(u, Intent.URI_INTENT_SCHEME).getStringExtra("browser_fallback_url"); } catch (Throwable ignored) { }
                if (fallback != null && (fallback.startsWith("https://") || fallback.startsWith("http://"))) { if (main) { mainUrl = fallback; v.loadUrl(fallback); } }
                else if (main) say("This link opens another app: not available here");
                return true;
            }
            if (!scheme.equals("http") && !scheme.equals("https") && !scheme.equals("about") && !scheme.equals("data")
                    && !scheme.equals("blob") && !scheme.equals("javascript") && !scheme.equals("file")) {
                if (main) say("Links of type " + scheme + ": do not open here");
                return true;
            }
            if (main) mainUrl = u;
            return false;
        }
        @Override public void onPageStarted(WebView v, String u, android.graphics.Bitmap icon) {
            url = clean(u); mainUrl = u; loading = true; error = ""; errorUrl = ""; cancelJs(); nav(); bump(); scheduleDraw();
        }
        @Override public void onPageFinished(WebView v, String u) { url = clean(u); loading = false; nav(); bump(); scheduleDraw(); }
        @Override public void doUpdateVisitedHistory(WebView v, String u, boolean reload) { url = clean(u); nav(); bump(); } // pages that change their address themselves
        @Override public void onReceivedError(WebView v, WebResourceRequest r, WebResourceError e) {
            if (!r.isForMainFrame()) return;
            error = plain(e.getErrorCode(), String.valueOf(e.getDescription()));
            errorUrl = String.valueOf(r.getUrl());
            bump();
        }
        @Override public void onReceivedSslError(WebView v, SslErrorHandler h, SslError e) {
            h.cancel();                                  // never proceeds past a bad certificate
            if (e.getUrl() != null && e.getUrl().equals(mainUrl)) {
                error = "The site's security certificate is not valid: the page was not opened";
                errorUrl = e.getUrl();
                loading = false;
                bump();
            }
        }
        @Override public boolean onRenderProcessGone(WebView v, RenderProcessGoneDetail d) {
            // the page's process died (out of memory, or a crash): a new WebView, the app goes on
            Log.w(TAG, "web: the page's renderer stopped (crash " + d.didCrash() + ")");
            final String last = url;
            ui.post(() -> rebuild(v, last));
            return true;
        }
    }

    private void rebuild(WebView dead, String last) {
        if (closed || dead != web) return;
        removeCustom();
        host.removeView(dead);
        try { dead.destroy(); } catch (Throwable ignored) { }
        web = makeWeb();
        if (canvasMode) web.getSettings().setOffscreenPreRaster(true);
        host.addView(web, 0, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        if (!canvasMode) web.requestFocus();
        appliedPause = false;
        applyPause();
        loading = false;
        canBack = canForward = false;
        error = "The page stopped (it needed too much memory, or failed)";
        errorUrl = last == null ? "" : last;
        bump();
        scheduleDraw();
    }

    private final class Chrome extends WebChromeClient {
        @Override public void onProgressChanged(WebView v, int p) { if (p != progress) { progress = p; bump(); } }
        @Override public void onReceivedTitle(WebView v, String t) { title = clean(t); bump(); }

        // full screen (a video's full-screen button, or requestFullscreen): the page's full-screen view fills the same surface
        @Override public void onShowCustomView(View view, CustomViewCallback cb) {
            if (custom != null) { cb.onCustomViewHidden(); return; }
            custom = view;
            customCallback = cb;
            customFrame = new FrameLayout(activity);
            customFrame.setBackgroundColor(Color.BLACK);
            customFrame.addView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            host.addView(customFrame, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            if (!canvasMode) view.requestFocus();
            fullscreen = true;
            bump();
            scheduleDraw();
        }
        @Override public void onHideCustomView() { removeCustom(); }

        @Override public void onPermissionRequest(PermissionRequest r) {
            StringBuilder what = new StringBuilder();
            for (String res : r.getResources()) {
                String w = PermissionRequest.RESOURCE_VIDEO_CAPTURE.equals(res) ? "the camera"
                        : PermissionRequest.RESOURCE_AUDIO_CAPTURE.equals(res) ? "the microphone"
                        : PermissionRequest.RESOURCE_PROTECTED_MEDIA_ID.equals(res) ? "protected video" : "a device";
                if (what.indexOf(w) < 0) what.append(what.length() > 0 ? " and " : "").append(w);
            }
            r.deny();
            say("The page asked for " + what + ": refused");
        }
        @Override public void onGeolocationPermissionsShowPrompt(String origin, GeolocationPermissions.Callback cb) {
            cb.invoke(origin, false, false);
            say("The page asked for your location: refused");
        }
        @Override public boolean onShowFileChooser(WebView v, ValueCallback<Uri[]> cb, FileChooserParams params) {
            cb.onReceiveValue(null);
            say("Picking files for a page is not available here");
            return true;
        }

        // the page's alert and confirm: a dialog in the app (Unity reads state(), answers with jsAnswer)
        @Override public boolean onJsAlert(WebView v, String u, String message, JsResult result) { ask("alert", message, result); return true; }
        @Override public boolean onJsConfirm(WebView v, String u, String message, JsResult result) { ask("confirm", message, result); return true; }
        @Override public boolean onJsPrompt(WebView v, String u, String message, String def, JsPromptResult result) {
            result.cancel();
            say("The page asked for text in a dialog: not available here");
            return true;
        }
        @Override public boolean onJsBeforeUnload(WebView v, String u, String message, JsResult result) { result.confirm(); return true; }
    }

    private void ask(String kind, String message, JsResult result) {
        cancelJs();
        js = result;
        jsId = ++jsSeq;
        jsKind = kind;
        jsMessage = clean(message);
        bump();
    }

    private void cancelJs() {
        if (js == null) return;
        JsResult r = js;
        js = null;
        jsKind = "";
        jsMessage = "";
        bump();
        try { r.cancel(); } catch (Throwable ignored) { }
    }

    private void removeCustom() {
        if (customFrame != null) {
            customFrame.removeAllViews();
            host.removeView(customFrame);
        }
        custom = null;
        customFrame = null;
        customCallback = null;
        if (fullscreen) { fullscreen = false; bump(); }
        if (web != null && !canvasMode) web.requestFocus();
        scheduleDraw();
    }

    private void exitFullscreenNow() {
        if (custom == null) return;
        WebChromeClient.CustomViewCallback cb = customCallback;
        removeCustom();
        if (cb != null) try { cb.onCustomViewHidden(); } catch (Throwable ignored) { }
    }

    /** The window closed or the app paused: the page's media pause, then the page and its timers. */
    private void applyPause() {
        boolean p = paused || activityPaused;
        if (web == null || p == appliedPause) return;
        appliedPause = p;
        final WebView w = web;
        if (p) {
            w.evaluateJavascript(PAUSE_MEDIA, v -> { if (appliedPause && w == web) { w.onPause(); w.pauseTimers(); } });
        } else {
            w.onResume();
            w.resumeTimers();
            scheduleDraw();
        }
    }

    // ---- the canvas path (fallback)

    private void scheduleDraw() {
        if (!canvasMode || drawPending || closed || paused || activityPaused) return;
        drawPending = true;
        ui.postDelayed(draw, 33);
    }

    // on the UI thread: the page (and a full-screen view over it) into the ImageReader through a hardware canvas
    private void drawFrame() {
        drawPending = false;
        ImageReader r = reader;
        View v = customFrame != null ? customFrame : web;
        if (!canvasMode || closed || paused || activityPaused || v == null || r == null) return;
        Surface surface = r.getSurface();
        Canvas c = null;
        try {
            c = surface.lockHardwareCanvas();
            c.drawColor(Color.WHITE);
            v.draw(c);
        } catch (Throwable t) {
            error = "draw: " + t;
            bump();
        } finally {
            if (c != null) try { surface.unlockCanvasAndPost(c); } catch (Throwable ignored) { }
        }
    }

    // ---- the copy thread

    // a new image: copied now, or (within 16 ms of the last copy) once that time comes, the newest then
    private void onImage(ImageReader r) {
        imagesSeen++;
        if (closed || r != reader) return;
        long due = lastCopy + minCopyMs;
        if (SystemClock.uptimeMillis() < due) {
            if (!drainPosted) { drainPosted = true; copier.postAtTime(drain, due); }
            return;
        }
        copyLatest(r);
    }

    private void drainLatest() {
        drainPosted = false;
        ImageReader r = reader;
        if (r != null && !closed) copyLatest(r);
    }

    // the newest image into the shared buffer, rows packed; while paused it is only taken (the queue stays empty)
    private void copyLatest(ImageReader r) {
        Image img = null;
        try {
            img = r.acquireLatestImage();
            if (img == null || paused || activityPaused) return;
            lastCopy = SystemClock.uptimeMillis();
            Image.Plane plane = img.getPlanes()[0];
            ByteBuffer src = plane.getBuffer();
            int stride = plane.getRowStride(), row = width * 4;
            if (img.getWidth() != width || img.getHeight() != height || plane.getPixelStride() != 4 || src.capacity() < stride * (height - 1) + row) return;
            lock.lock();
            try {
                pixels.clear();
                ByteBuffer line = src.duplicate();
                if (stride == row) {
                    line.clear();
                    line.limit(row * height);
                    pixels.put(line);
                } else {
                    for (int y = 0; y < height; y++) {
                        line.clear();
                        line.position(y * stride);
                        line.limit(y * stride + row);
                        pixels.put(line);
                    }
                }
                frame++;
            } finally {
                lock.unlock();
            }
        } catch (IllegalStateException closedOrFull) {
            // the reader closed (window gone) or every image is held: the next one comes
        } catch (Throwable t) {
            error = "copy: " + t;
            bump();
        } finally {
            if (img != null) img.close();
        }
    }

    // ---- the Unity side (its main thread)

    public ByteBuffer pixels() { return pixels; }
    public int width() { return width; }
    public int height() { return height; }

    /** Each Unity frame, never waits, allocates nothing. Bit 1: a new frame is in pixels() and the buffer is locked
     *  (read it, then call endRead); bit 2: the state changed since the last state(). While the copy thread writes,
     *  bit 1 stays off (Unity takes the frame next time). */
    public int poll() {
        int r = stateVersion.get() != stateSeen ? 2 : 0;
        if (frame != taken && lock.tryLock()) {
            if (frame != taken) { taken = frame; return r | 1; }
            lock.unlock();
        }
        return r;
    }

    public void endRead() { if (lock.isHeldByCurrentThread()) lock.unlock(); }

    /** Tab separated: url, title, progress, can go back, can go forward, loading, the error, a refused sign-in link
     *  (once), a notice (once), full screen, the capture path (display or canvas), the page dialog's kind (alert,
     *  confirm or empty), its id, its message. */
    public String state() {
        stateSeen = stateVersion.get();
        return url + "\t" + title + "\t" + progress + "\t" + (canBack ? 1 : 0) + "\t" + (canForward ? 1 : 0) + "\t" + (loading ? 1 : 0)
                + "\t" + error + "\t" + external.getAndSet("") + "\t" + clean(notice.getAndSet("")) + "\t" + (fullscreen ? 1 : 0) + "\t" + mode
                + "\t" + jsKind + "\t" + jsId + "\t" + jsMessage;
    }

    /** The pages that must not load in this WebView (sign-in pages: their session would stay in the app's cookies):
     *  hosts (a host or any of its subdomains) and path parts, one per line. A link to one is not followed; state()
     *  reports it once and Unity opens it in the system browser. */
    public void setExternal(String hosts, String paths) {
        externalHosts = hosts == null || hosts.isEmpty() ? new String[0] : hosts.split("\n");
        externalPaths = paths == null || paths.isEmpty() ? new String[0] : paths.split("\n");
    }

    private boolean isExternal(String u) {
        Uri uri = Uri.parse(u);
        String host = uri.getHost() == null ? "" : uri.getHost().toLowerCase();
        String path = uri.getPath() == null ? "" : uri.getPath().toLowerCase();
        if (host.isEmpty() || host.equals("localhost") || host.startsWith("127.")) return false;
        for (String h : externalHosts) if (host.equals(h) || host.endsWith("." + h)) return true;
        for (String p : externalPaths) if (path.contains(p)) return true;
        return false;
    }

    /** Copies per second at most (the texture's upload rate; 60 by default). */
    public void setMaxFps(int fps) { minCopyMs = Math.max(8, 1000 / Math.max(1, fps)); }

    public void load(final String u) {
        ui.post(() -> {
            if (web == null) return;
            cancelJs();
            exitFullscreenNow();
            error = ""; errorUrl = ""; mainUrl = u;
            bump();
            web.loadUrl(u);
        });
    }

    public void loadHtml(final String html, final String baseUrl) {
        ui.post(() -> {
            if (web == null) return;
            cancelJs();
            exitFullscreenNow();
            error = ""; errorUrl = "";
            bump();
            web.loadDataWithBaseURL(baseUrl, html, "text/html", "utf-8", null);
        });
    }

    /** Back: out of full screen first, then the page before. */
    public void back() { ui.post(() -> { if (web == null) return; if (custom != null) { exitFullscreenNow(); return; } cancelJs(); if (web.canGoBack()) web.goBack(); }); }
    public void forward() { ui.post(() -> { if (web != null && web.canGoForward()) { cancelJs(); web.goForward(); } }); }

    /** Reload; after a failed page (or a stopped renderer) its address again. */
    public void reload() {
        ui.post(() -> {
            if (web == null) return;
            cancelJs();
            String again = errorUrl;
            if (!again.isEmpty() && (web.getUrl() == null || !error.isEmpty())) { error = ""; errorUrl = ""; mainUrl = again; bump(); web.loadUrl(again); }
            else web.reload();
        });
    }

    public void stop() { ui.post(() -> { if (web != null) web.stopLoading(); }); }

    /** +1 zooms in, -1 out; a page that does not zoom (a fixed viewport) gets larger or smaller text instead. */
    public void zoom(final int dir) {
        ui.post(() -> {
            if (web == null || custom != null || dir == 0) return;
            boolean ok = dir > 0 ? web.zoomIn() : web.zoomOut();
            if (ok) return;
            WebSettings s = web.getSettings();
            int tz = s.getTextZoom(), next = Math.max(60, Math.min(250, tz + (dir > 0 ? 15 : -15)));
            if (next != tz) s.setTextZoom(next);
            scheduleDraw();
        });
    }

    public void exitFullscreen() { ui.post(this::exitFullscreenNow); }

    /** The answer to the page's dialog `id` (OK or Cancel); an older dialog's answer is ignored. */
    public void jsAnswer(final int id, final boolean ok) {
        ui.post(() -> {
            if (js == null || id != jsId) return;
            JsResult r = js;
            js = null;
            jsKind = "";
            jsMessage = "";
            bump();
            try { if (ok) r.confirm(); else r.cancel(); } catch (Throwable ignored) { }
        });
    }

    // the page's view under a point: the full-screen view when there is one
    private View target() { return custom != null ? custom : web; }

    /** A touch in page pixels: 0 down, 1 up, 2 move (MotionEvent's actions). Timed when Unity sends it, so the page
     *  measures a drag's speed right (a fling goes on by itself). */
    public void touch(final int action, final float x, final float y) {
        final long t = SystemClock.uptimeMillis();
        if (action == MotionEvent.ACTION_DOWN) downTime = t;
        final long down = downTime;
        ui.post(() -> {
            if (host == null || web == null) return;
            if (action == MotionEvent.ACTION_DOWN && !canvasMode && custom == null && !web.hasFocus()) web.requestFocus();
            MotionEvent ev = MotionEvent.obtain(down, t, action, x, y, 0);
            ev.setSource(InputDevice.SOURCE_TOUCHSCREEN);
            host.dispatchTouchEvent(ev);               // the view under the point gets it (the full-screen view on top)
            ev.recycle();
            scheduleDraw();
        });
    }

    /** The pointer over the page without pressing (hover styles, menus that open on hover). */
    public void hover(final float x, final float y) {
        final long t = SystemClock.uptimeMillis();
        ui.post(() -> {
            if (host == null) return;
            MotionEvent ev = MotionEvent.obtain(t, t, MotionEvent.ACTION_HOVER_MOVE, x, y, 0);
            ev.setSource(InputDevice.SOURCE_MOUSE);
            host.dispatchGenericMotionEvent(ev);
            ev.recycle();
        });
    }

    /** The wheel: positive dy scrolls the page down. */
    public void wheel(final float x, final float y, final float dy) {
        final long t = SystemClock.uptimeMillis();
        ui.post(() -> {
            if (host == null) return;
            MotionEvent.PointerProperties[] props = { new MotionEvent.PointerProperties() };
            props[0].id = 0;
            props[0].toolType = MotionEvent.TOOL_TYPE_MOUSE;
            MotionEvent.PointerCoords[] coords = { new MotionEvent.PointerCoords() };
            coords[0].x = x;
            coords[0].y = y;
            coords[0].setAxisValue(MotionEvent.AXIS_VSCROLL, -dy);
            MotionEvent ev = MotionEvent.obtain(t, t, MotionEvent.ACTION_SCROLL, 1, props, coords, 0, 0, 1f, 1f, 0, 0, InputDevice.SOURCE_MOUSE, 0);
            host.dispatchGenericMotionEvent(ev);
            ev.recycle();
            scheduleDraw();
        });
    }

    /** Text into the focused field of the page. */
    public void text(final String s) {
        ui.post(() -> { if (web != null) web.evaluateJavascript("document.execCommand('insertText', false, " + JSONObject.quote(s) + ");", null); });
    }

    /** A key (KeyEvent codes: 66 enter, 67 backspace, 61 tab, 111 escape, 19-22 arrows); escape leaves full screen. */
    public void key(final int code) {
        final long t = SystemClock.uptimeMillis();
        ui.post(() -> {
            if (web == null) return;
            if (code == KeyEvent.KEYCODE_ESCAPE && custom != null) { exitFullscreenNow(); return; }
            View v = target();
            v.dispatchKeyEvent(new KeyEvent(t, t, KeyEvent.ACTION_DOWN, code, 0));
            v.dispatchKeyEvent(new KeyEvent(t, t, KeyEvent.ACTION_UP, code, 0));
            scheduleDraw();
        });
    }

    /** The window closed: the page's media pause, then the page stops (timers, drawing, copying); open again: it goes on. */
    public void setPaused(final boolean p) {
        paused = p;
        ui.post(this::applyPause);
    }

    public void close() {
        closed = true;
        ui.post(() -> {
            try {
                cancelJs();
                if (life != null) activity.getApplication().unregisterActivityLifecycleCallbacks(life);
                removeCustom();
                if (presentation != null) presentation.dismiss();
                if (display != null) display.release();
                if (host != null && host.getParent() instanceof ViewGroup) ((ViewGroup) host.getParent()).removeView(host);
                if (web != null) { web.stopLoading(); web.destroy(); }
            } catch (Throwable ignored) { }
            web = null;
            presentation = null;
            display = null;
            final ImageReader r = reader;
            reader = null;
            copier.post(() -> { if (r != null) r.close(); thread.quitSafely(); });
        });
    }
}
