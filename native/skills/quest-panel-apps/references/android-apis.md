# Files, threads, network and storage in a 2D panel app

What Android gives a plain Java app on the headset (Android 14, API 34), and how to use it without AndroidX. The
headset has no Google Play Services: nothing from `com.google.android.gms` works.

## The user's files: the system file picker (Storage Access Framework)

The user picks every file or folder in the system's picker; the app gets access to exactly those, with no permission
in the manifest. Never ask for `READ_EXTERNAL_STORAGE`, `WRITE_EXTERNAL_STORAGE` or `MANAGE_EXTERNAL_STORAGE`.

| Need | Intent | Result |
|---|---|---|
| open one or more files | `ACTION_OPEN_DOCUMENT` + `CATEGORY_OPENABLE`, a MIME type (`"*/*"`, `"text/*"`, `"image/*"`), `EXTRA_ALLOW_MULTIPLE` | `getData()` (one) or `getClipData()` (several): `content://` URIs |
| save a new file | `ACTION_CREATE_DOCUMENT` + `CATEGORY_OPENABLE`, its type, `EXTRA_TITLE` (the proposed name) | the new file's URI: write with `openOutputStream(uri, "wt")` |
| a folder (unzip into it, export many files) | `ACTION_OPEN_DOCUMENT_TREE` | a tree URI: `DocumentsContract.createDocument(resolver, DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree)), mime, name)` |

```java
Intent pick = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
        .putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
try {
    startActivityForResult(pick, 1);
} catch (android.content.ActivityNotFoundException e) {
    Mcx.log("no file picker: %s", e); // say it in the window too
}
```

```java
@Override
protected void onActivityResult(int code, int result, Intent data) {
    super.onActivityResult(code, result, data);
    if (result != RESULT_OK || data == null) return; // the user closed the picker
    Uri one = data.getData();
    android.content.ClipData many = data.getClipData();
    Mcx.log("picked %s", many != null ? many.getItemCount() + " files" : String.valueOf(one));
}
```

A picked file's name and size, and its bytes:

```java
Uri uri = getIntent().getData(); // any content:// URI from the picker
try (android.database.Cursor c = getContentResolver().query(uri, new String[]{android.provider.OpenableColumns.DISPLAY_NAME,
        android.provider.OpenableColumns.SIZE}, null, null, null)) {
    if (c != null && c.moveToFirst()) Mcx.log("%s, %s bytes", c.getString(0), c.isNull(1) ? "?" : c.getString(1));
}
try (InputStream in = getContentResolver().openInputStream(uri)) {
    byte[] buf = new byte[1 << 16];
    for (int n; (n = in.read(buf)) > 0; ) { /* use buf[0..n) */ }
}
```

Access to a picked URI lasts while the activity lives. To open it again after a restart, keep it:
`getContentResolver().takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)` and store
`uri.toString()`. A half-written file after an error or a stop: `DocumentsContract.deleteDocument(resolver, uri)`
(the zipper does).

## Threads

The UI thread draws the window: never do file, network or long work there (Android stops network calls on it with
`NetworkOnMainThreadException`, and the window freezes). One worker and a handler back to the UI:

```java
java.util.concurrent.ExecutorService worker = java.util.concurrent.Executors.newSingleThreadExecutor();
Handler ui = new Handler(Looper.getMainLooper());
worker.execute(() -> {
    long sum = 0;
    for (int i = 0; i < 1_000_000; i++) sum += i; // the slow part
    long result = sum;
    ui.post(() -> Mcx.log("done: %d", result)); // views only on the UI thread
});
```

Progress: post at most ten updates a second (the zipper keeps the last post time). A stop button sets a `volatile`
flag the worker checks in its loop. `onDestroy`: set the flag and `worker.shutdownNow()`.

## Network

The template's manifest has `android.permission.INTERNET` (also for its log lines). Use `HttpURLConnection` on the
worker, with timeouts; HTTPS works with the system's certificates; plain `http://` is blocked by default for apps that
target 28 and later (`android:usesCleartextTraffic="true"` on `<application>` only when a device on the local network
needs it; raw sockets, like `Mcx.log`'s, are not affected).

```java
java.net.HttpURLConnection c = (java.net.HttpURLConnection) new java.net.URL("https://example.org/data.json").openConnection();
c.setConnectTimeout(8000);
c.setReadTimeout(15000);
try (InputStream in = c.getInputStream()) {
    ByteArrayOutputStream all = new ByteArrayOutputStream();
    byte[] buf = new byte[1 << 14];
    for (int n; (n = in.read(buf)) > 0; ) all.write(buf, 0, n);
    String body = all.toString("UTF-8");
    org.json.JSONObject j = new org.json.JSONObject(body);
    Mcx.log("got %d keys", j.length());
} finally {
    c.disconnect();
}
```

`org.json` is part of Android (no library needed). No GMS-based sign-in, maps, push or ads.

## The app's own data

| What | Where |
|---|---|
| settings, small values | `getSharedPreferences("app", MODE_PRIVATE)`, `.edit().putString(k, v).apply()` |
| documents, lists (JSON) | files in `getFilesDir()` (private, kept until uninstall); write to a temporary file, then rename |
| a cache | `getCacheDir()` (the system may clear it) |
| something the user should see as a file | save it with `ACTION_CREATE_DOCUMENT` (above) |

## Java and Android here

- javac compiles **Java 17** (records, `var`, switch expressions, text blocks, lambdas); D8 turns it into code for
  Android 12L and later (`minSdkVersion` 32).
- `android.*` is checked against Android 14 (API 34). `java.*` is checked against Java 17, which has a few classes and
  methods Android lacks: they compile and crash with `NoSuchMethodError` / `NoClassDefFoundError` on the headset. Safe:
  `java.io`, `java.nio.file`, `java.nio.charset`, `java.util` (collections, `Optional`, streams), `java.util.concurrent`,
  `java.util.zip`, `java.util.regex`, `java.net` (sockets, `URL`, `HttpURLConnection`), `java.text`, `java.time`.
  Not there: `java.net.http`, `java.lang.Thread.ofVirtual`, `javax.swing` / `java.awt`, `java.lang.management`.
- Plain Java libraries: their `.jar` in `libs/` (compiled against and dexed in). Not `.aar` files (they carry resources
  and manifests), not Kotlin libraries (no Kotlin runtime), not anything that needs Google Play Services.
- Views: `android.widget` (`TextView`, `Button`, `EditText`, `ImageView`, `ProgressBar`, `SeekBar`, `Switch`,
  `ListView`, `ScrollView`, `LinearLayout`, `FrameLayout`, `GridLayout`), `android.app.AlertDialog`, and your own
  `View` subclass with `onDraw(Canvas)` for charts and drawings. No RecyclerView, no ConstraintLayout (AndroidX).
- Sound: `android.media.ToneGenerator`, `SoundPool`, `MediaPlayer` work as on a phone. Speech, camera and the
  microphone need runtime permissions (`requestPermissions`): add them only when the app needs them.
