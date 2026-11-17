package com.metacodex.paneltemplate;

import android.app.Activity;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/**
 * The 2D panel template (mcx-app new --panel): one window with a counter, built from plain Android views in code.
 * Replace the card with your app; keep Mcx.start, Mcx.topBar and Mcx.log (Back / Report to MetaCodex, `mcx-app logs`).
 * The window is resizable (360 to about 1280dp wide): the content scrolls and the rows wrap, so it works at any size.
 */
public final class MainActivity extends Activity {
    private int count;
    private TextView number, status;

    @Override
    protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        Mcx.start(this);
        count = saved == null ? 0 : saved.getInt("count");
        setContentView(build());
        show("Ready");
    }

    private View build() {
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        int pad = Mcx.dp(this, 32);
        page.setPadding(pad, Mcx.dp(this, 24), pad, pad);
        page.addView(Mcx.topBar(this, Mcx.label()));

        TextView intro = Mcx.text(this, "A 2D app in its own window. Point and pinch, or point and click with a controller, "
                + "to press a button. Move the window by its bar and resize it from its corners.", 18, Mcx.DIM, false);
        page.addView(intro, margins(0, 12, 0, 24));

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackground(Mcx.round(this, Mcx.SURFACE, Mcx.LINE));
        int cp = Mcx.dp(this, 24);
        card.setPadding(cp, cp, cp, cp);
        card.addView(Mcx.text(this, "Counter", 20, Mcx.TEXT, true));
        number = Mcx.text(this, "0", 64, Mcx.ACCENT, true);
        card.addView(number, margins(0, 8, 0, 16));

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        Button plus = Mcx.button(this, "Add one", true);
        plus.setOnClickListener(v -> {
            count++;
            Mcx.log("count %d", count);
            show("Added one");
        });
        Button reset = Mcx.button(this, "Reset", false);
        reset.setOnClickListener(v -> {
            count = 0;
            Mcx.log("reset");
            show("Back to zero");
        });
        row.addView(plus);
        row.addView(reset, margins(16, 0, 0, 0));
        card.addView(row);
        status = Mcx.text(this, "", 16, Mcx.DIM, false);
        card.addView(status, margins(0, 16, 0, 0));
        page.addView(card);

        ScrollView scroll = new ScrollView(this); // a small window scrolls instead of cutting the content off
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(Mcx.BG);
        scroll.addView(page);
        return scroll;
    }

    private void show(String what) {
        number.setText(String.valueOf(count));
        status.setText(what);
    }

    private LinearLayout.LayoutParams margins(int l, int t, int r, int b) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.setMargins(Mcx.dp(this, l), Mcx.dp(this, t), Mcx.dp(this, r), Mcx.dp(this, b));
        return lp;
    }

    @Override
    protected void onSaveInstanceState(Bundle out) {
        super.onSaveInstanceState(out);
        out.putInt("count", count);
    }
}
