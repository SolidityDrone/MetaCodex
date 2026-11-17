# Designing a 2D panel app: a window in the room, not a phone screen

From Meta's rules for Android apps on the headset (android-apps design-requirements, gaze-app-config, panel sizing;
checked 2026-11-17) and what the `Mcx` helpers already do. Requirements first, then how.

## The window

- **Size.** It opens at the `<layout>` default (the template: 1024 x 640 dp). The user resizes it, and with Look and
  Pinch the system scales the window (48dp is about 3 degrees of view) and may shrink it to fit: the layout must stay
  usable **from 360dp to about 1280dp wide** and down to the declared `minHeight`. Never a fixed-size layout.
- **Scroll, wrap, reflow.** Put the page in a `ScrollView` (the template does); rows of buttons that do not fit wrap
  or stack; long names ellipsize in the middle (`TextUtils.TruncateAt.MIDDLE`). Check it at 360dp: narrow the window
  in your head, or set `android:defaultWidth="360dp"` for one build.
- **Resizing keeps the state.** The template's `android:configChanges` makes a resize re-lay the views instead of
  restarting the activity; still save small state in `onSaveInstanceState` (the counter does).
- **One window, one job.** A second screen inside the window (details, settings) needs an in-app back button: the
  headset has no system back button for every input. Replace the content (`setContentView`) or show and hide sections;
  a dialog (`AlertDialog`) for a short question.

## Input and targets

- Targets **at least 48dp x 48dp, 60dp for the main controls** (`Mcx.button` is 60dp high and 120dp wide at least);
  **16dp between** neighbouring controls; small icons get invisible padding (hit slop) up to 48dp.
- Every control has **hovered, focused and pressed states** (`Mcx.button` has them: the ray or gaze hovers before the
  pinch). Your own clickable views: a `StateListDrawable` with `android.R.attr.state_hovered` like `Mcx.button`.
- Input is click / touch: `setOnClickListener` for taps, `OnTouchListener` only for drags (sliders, drawing). No long
  press for an important action (hard with a pinch); no hover-only information.
- Text input: an `EditText` opens the system keyboard; give it a hint (`setHint`) and the right `setInputType`.

## Look

- The `Mcx` palette: background `Mcx.BG`, cards `Mcx.SURFACE` with a `Mcx.LINE` edge (`Mcx.round`), text `Mcx.TEXT`, secondary
  `Mcx.DIM`, accent `Mcx.ACCENT`, states `Mcx.OK` / `Mcx.WARN` / `Mcx.DANGER`. Contrast at least 4.5:1 for text and
  3:1 for controls (the palette has it); **no pure white or black** (harsh in a headset); colours look more saturated
  in the headset than on a phone: keep accents few.
- **Type**: body 18sp, secondary 16sp, never under 14sp; titles 20-26sp; numbers that matter 48-64sp. Medium or bold
  weights read better than light ones (`Mcx.text(..., bold)` uses sans-serif-medium).
- **Layout**: 24-32dp page padding, 20-24dp inside cards, 8dp between list rows; one primary (accent) button per area.
- **Feedback**: every action answers in the window within 100 ms (a status line, a disabled button while busy, a
  progress bar for anything over a second) and in the log (`Mcx.log`). Errors say what happened and what to do, in the
  window, in plain words.

## Patterns (on the `Mcx` helpers)

A card with a title and a row of buttons that wraps under each other when the window is narrow:

```java
LinearLayout card = new LinearLayout(this);
card.setOrientation(LinearLayout.VERTICAL);
card.setBackground(Mcx.round(this, Mcx.SURFACE, Mcx.LINE));
int p = Mcx.dp(this, 24);
card.setPadding(p, p, p, p);
card.addView(Mcx.text(this, "Units", 20, Mcx.TEXT, true));
boolean narrow = getResources().getConfiguration().screenWidthDp < 600;
LinearLayout row = new LinearLayout(this);
row.setOrientation(narrow ? LinearLayout.VERTICAL : LinearLayout.HORIZONTAL);
for (String unit : new String[]{"Metres", "Feet", "Inches"}) {
    Button b = Mcx.button(this, unit, false);
    b.setOnClickListener(v -> Mcx.log("unit %s", unit));
    LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
    lp.setMargins(0, Mcx.dp(this, 8), Mcx.dp(this, 16), 0);
    row.addView(b, lp);
}
card.addView(row);
```

When the width changes (the user resizes the window), rebuild what depends on it:

```java
@Override
public void onConfigurationChanged(android.content.res.Configuration c) {
    super.onConfigurationChanged(c);
    Mcx.log("window %d x %d dp", c.screenWidthDp, c.screenHeightDp);
    // e.g. setContentView(build()); then show the current state again
}
```

A short question:

```java
new android.app.AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
        .setTitle("Delete the list?")
        .setMessage("The 12 entries go. This cannot be undone.")
        .setPositiveButton("Delete", (d, w) -> Mcx.log("list deleted"))
        .setNegativeButton("Keep", null)
        .show();
```

## Self-review (before the first install, and before each one after a big change)

1. Every action of the request has a control, a visible answer and a log line.
2. At 360dp wide nothing is cut off or overlaps; at 1280dp nothing is stretched across the whole width (cap wide text
   columns with a `maxWidth` or side padding).
3. Targets 48dp+ (main ones 60dp), 16dp apart; hover states on everything clickable.
4. Text 18sp body, nothing under 14sp; the palette; no pure white or black; one accent action per area.
5. Slow work off the UI thread, with progress and a way to stop it; errors in plain words in the window.
6. The top row: the app's name, and Back / Report (they appear when MetaCodex launched it).
7. An icon of its own; the label is the app's name; `mcx-app verify` says `RESULT: verify ok`.
