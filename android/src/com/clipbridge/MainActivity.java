package com.clipbridge;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.Settings;
import android.util.TypedValue;
import android.view.Gravity;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.List;

/** Shows the pairing code and whether the PC is connected. */
public class MainActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private TextView status;
    private TextView last;
    private TextView addresses;
    private Button battery;

    private final Runnable refresh = this::refreshScreen;

    private void refreshScreen() {
        boolean on = BridgeService.connected;
        status.setText(on ? "Connected to your PC" + BridgeService.connectedVia : "Waiting for your PC");
        status.setTextColor(on ? Color.rgb(46, 125, 50) : Color.rgb(198, 120, 0));
        last.setText("Last received: " + BridgeService.lastClip);
        addresses.setText(BridgeService.bluetoothState + "\nThis tablet's address: " + String.join(", ", BridgeService.localAddresses()));
        boolean exempt = getSystemService(PowerManager.class).isIgnoringBatteryOptimizations(getPackageName());
        battery.setVisibility(exempt ? Button.GONE : Button.VISIBLE);
        handler.postDelayed(refresh, 1000);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER_HORIZONTAL);
        int pad = dp(32);
        root.setPadding(pad, pad, pad, pad);

        status = text("", 28, true);
        root.addView(status);
        last = text("", 16, false);
        root.addView(last);

        TextView label = text("Pairing code. Type this into ClipBridge on your PC:", 16, false);
        label.setPadding(0, dp(32), 0, dp(8));
        root.addView(label);

        TextView code = text(BridgeService.pairingCode(this), 32, true);
        code.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        code.setTextIsSelectable(true);
        root.addView(code);

        addresses = text("", 14, false);
        addresses.setPadding(0, dp(32), 0, dp(16));
        root.addView(addresses);

        battery = new Button(this);
        battery.setText("Keep running in the background");
        battery.setOnClickListener(v -> startActivity(
                new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:" + getPackageName()))));
        root.addView(battery);

        ScrollView scroll = new ScrollView(this);
        scroll.addView(root);
        setContentView(scroll);

        List<String> missing = new ArrayList<>();
        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.POST_NOTIFICATIONS);
        }
        if (Build.VERSION.SDK_INT >= 31
                && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.BLUETOOTH_CONNECT);
        }
        if (!missing.isEmpty()) requestPermissions(missing.toArray(new String[0]), 1);
        startForegroundService(new Intent(this, BridgeService.class));
    }

    @Override
    protected void onResume() {
        super.onResume();
        handler.post(refresh);
    }

    @Override
    protected void onPause() {
        handler.removeCallbacks(refresh);
        super.onPause();
    }

    private TextView text(String value, int sp, boolean bold) {
        TextView t = new TextView(this);
        t.setText(value);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setGravity(Gravity.CENTER);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    private int dp(int value) {
        return (int) (value * getResources().getDisplayMetrics().density);
    }
}
