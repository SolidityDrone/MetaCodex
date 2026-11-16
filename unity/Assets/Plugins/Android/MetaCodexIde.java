package com.pnncl.metacodex.ide;

import android.os.Handler;
import android.os.Looper;
import android.webkit.CookieManager;

/**
 * The code editor's sign-in (docs/ide.md, ShellIde.cs): puts the editor's session cookie into the app's WebView cookie
 * store, so the WEB window opens the editor signed in and nobody types its password. The store (CookieManager) is
 * process-wide: the cookie is set for the editor's own address only, host-only (no Domain), HttpOnly (no page script
 * reads it) and SameSite=Strict (no other site's page sends it along). It runs on Android's UI thread with the async
 * setCookie, so Unity's thread never waits for the WebView's cookie thread; Unity polls state(). Kept apart from the
 * browser's MetaCodexWebView.
 */
public final class MetaCodexIde {
    private static volatile String state = "";

    private MetaCodexIde() { }

    /** "" before the first call, "pending", then "ok" or "error: ...". */
    public static String state() { return state; }

    /** Sets `cookie` ("name=value; Path=/; ...") for `url` ("http://127.0.0.1:47820/"). */
    public static void setSession(final String url, final String cookie) {
        state = "pending";
        new Handler(Looper.getMainLooper()).post(() -> {
            try {
                final CookieManager cm = CookieManager.getInstance();
                cm.setCookie(url, cookie, ok -> {
                    cm.flush();
                    state = Boolean.TRUE.equals(ok) ? "ok" : "error: the cookie store refused it";
                });
            } catch (Throwable t) {
                state = "error: " + t;
            }
        });
    }
}
