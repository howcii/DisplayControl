package com.displaycontrol.androidagent;

import android.app.Activity;
import android.app.admin.DevicePolicyManager;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.PowerManager;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.CompoundButton;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

/** Android Agent 配置面板。只保留现场真正需要的按钮和状态。 */
public class MainActivity extends Activity {
    private ConfigStore config;
    private ScreenController screenController;
    private EditText serverInput;
    private EditText deviceIdInput;
    private EditText deviceNameInput;
    private EditText pollInput;
    private CheckBox autoStartCheck;
    private TextView statusText;
    private TextView permissionText;

    private final BroadcastReceiver statusReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            refreshStatus();
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        config = new ConfigStore(this);
        screenController = new ScreenController(this, config);
        setContentView(buildContent());
        loadConfigIntoForm();
        refreshStatus();
    }

    @Override
    protected void onStart() {
        super.onStart();
        registerReceiver(statusReceiver, new IntentFilter(Constants.ACTION_STATUS_CHANGED));
        refreshStatus();
    }

    @Override
    protected void onResume() {
        super.onResume();
        refreshStatus();
    }

    @Override
    protected void onStop() {
        try {
            unregisterReceiver(statusReceiver);
        } catch (Throwable ignored) {
        }
        super.onStop();
    }

    private View buildContent() {
        ScrollView scrollView = new ScrollView(this);
        scrollView.setFillViewport(true);
        scrollView.setBackgroundColor(Color.rgb(244, 247, 251));

        FrameLayout contentFrame = new FrameLayout(this);
        scrollView.addView(contentFrame, new ScrollView.LayoutParams(
            ScrollView.LayoutParams.MATCH_PARENT,
            ScrollView.LayoutParams.WRAP_CONTENT));

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(18), dp(18), dp(18), dp(24));
        FrameLayout.LayoutParams rootParams = new FrameLayout.LayoutParams(
            contentWidth(),
            FrameLayout.LayoutParams.WRAP_CONTENT,
            Gravity.TOP | Gravity.CENTER_HORIZONTAL);
        contentFrame.addView(root, rootParams);

        LinearLayout header = card();
        TextView title = text(getString(R.string.app_title), 26, Color.rgb(15, 23, 42), true);
        TextView subtitle = text("Android 显示端 · V1.6.9", 15, Color.rgb(100, 116, 139), false);
        header.addView(title);
        header.addView(subtitle);
        root.addView(header);

        LinearLayout configCard = card();
        configCard.addView(sectionTitle("连接配置"));
        serverInput = input("例如：http://192.168.1.100:8080");
        deviceIdInput = input("例如：android-screen-01");
        deviceNameInput = input("例如：安卓展示屏01");
        pollInput = input("默认 1000，单位毫秒");
        pollInput.setInputType(InputType.TYPE_CLASS_NUMBER);
        configCard.addView(row("服务器地址", serverInput));
        configCard.addView(row("设备编号", deviceIdInput));
        configCard.addView(row("设备名称", deviceNameInput));
        configCard.addView(row("轮询间隔", pollInput));
        autoStartCheck = new CheckBox(this);
        autoStartCheck.setText("开机后自动启动 Agent 服务");
        autoStartCheck.setTextColor(Color.rgb(51, 65, 85));
        autoStartCheck.setTextSize(15);
        configCard.addView(autoStartCheck);
        root.addView(configCard);

        LinearLayout statusCard = card();
        statusCard.addView(sectionTitle("当前状态"));
        statusText = text("", 14, Color.rgb(51, 65, 85), false);
        statusText.setLineSpacing(4, 1.05f);
        statusText.setPadding(dp(12), dp(12), dp(12), dp(12));
        statusText.setBackground(round(Color.rgb(239, 246, 255), Color.rgb(191, 219, 254), 14, 1));
        statusCard.addView(statusText);
        root.addView(statusCard);

        LinearLayout operationCard = card();
        operationCard.addView(sectionTitle("日常操作"));
        operationCard.addView(buttonRow(
            blueButton("保存配置", v -> saveConfig()),
            greenButton("启动/重启 Agent", v -> {
                saveConfig();
                startServiceCompat(Constants.ACTION_START_SERVICE);
                toast("Agent 服务启动中");
            })));
        operationCard.addView(buttonRow(
            whiteButton("停止 Agent 服务", v -> {
                startServiceCompat(Constants.ACTION_STOP_SERVICE);
                toast("已请求停止 Agent 服务");
            }),
            whiteButton("测试连接服务器", v -> testConnection())));
        operationCard.addView(buttonRow(
            whiteButton("本机黑幕测试", v -> openLocalBlackout()),
            whiteButton("解除黑幕/亮屏", v -> clearBlackoutAndWake())));
        operationCard.addView(buttonRow(
            whiteButton("本机休眠测试", v -> testLocalStandby()),
            whiteButton("恢复息屏时间", v -> {
                screenController.restoreSleepTimeoutFallback();
                refreshStatus();
                toast("已请求恢复系统息屏时间");
            })));
        root.addView(operationCard);

        LinearLayout permissionCard = card();
        permissionCard.addView(sectionTitle("权限"));
        permissionText = text("", 15, Color.rgb(51, 65, 85), false);
        permissionText.setLineSpacing(3, 1.0f);
        permissionCard.addView(permissionText);
        permissionCard.addView(buttonRow(
            whiteButton("启用系统休眠权限", v -> requestDeviceAdmin()),
            whiteButton("允许系统设置", v -> requestWriteSettings())));
        permissionCard.addView(buttonRow(
            whiteButton("电池后台设置", v -> requestBatteryOptimization()),
            whiteButton("刷新权限状态", v -> refreshStatus())));
        root.addView(permissionCard);

        autoStartCheck.setOnCheckedChangeListener((CompoundButton buttonView, boolean isChecked) -> saveConfig(false));
        return scrollView;
    }

    private void loadConfigIntoForm() {
        serverInput.setText(config.getServerUrl());
        deviceIdInput.setText(config.getDeviceId());
        deviceNameInput.setText(config.getDeviceName());
        pollInput.setText(String.valueOf(config.getPollMs()));
        autoStartCheck.setChecked(config.isAutoStartEnabled());
    }

    private void saveConfig() {
        saveConfig(true);
    }

    private void saveConfig(boolean showToast) {
        int pollMs = Constants.DEFAULT_POLL_MS;
        try {
            pollMs = Integer.parseInt(pollInput.getText().toString().trim());
        } catch (Throwable ignored) {
        }
        config.save(
            serverInput.getText().toString(),
            deviceIdInput.getText().toString(),
            deviceNameInput.getText().toString(),
            pollMs,
            autoStartCheck.isChecked());
        loadConfigIntoForm();
        refreshStatus();
        if (showToast) {
            toast("配置已保存");
        }
    }

    private void refreshStatus() {
        StatusSnapshot snapshot = screenController.observe();
        JSONObject status = config.getLastStatusJson();
        JSONObject result = config.getLastResultJson();
        String connection = status.optString("connectionState", "未启动");
        String detail = status.optString("detail", "尚未连接服务器。");
        String screen = status.optString("screenStatus", snapshot.state);
        boolean verified = status.optBoolean("screenStatusVerified", snapshot.verified);
        long updatedAt = status.optLong("updatedAt", 0L);
        String lastResult = result.optString("commandName", "").length() == 0
            ? "无"
            : result.optString("commandName", "") + " / " + result.optString("stage", "") + " / " + result.optString("screenStatusDetails", "");
        statusText.setText(
            "连接：" + connection + "\n"
                + "详情：" + detail + "\n"
                + "设备：" + config.getDeviceId() + " / " + config.getDeviceName() + "\n"
                + "屏幕：" + displayState(screen, verified) + "\n"
                + "回执：" + lastResult + "\n"
                + "更新：" + (updatedAt == 0L ? "无" : new java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss", java.util.Locale.CHINA).format(new java.util.Date(updatedAt))));

        permissionText.setText(
            "休眠权限：" + (screenController.isDeviceAdminActive() ? "已启用" : "未启用") + "\n"
                + "系统设置：" + (screenController.canWriteSystemSettings() ? "已允许" : "未允许") + "\n"
                + "后台运行：" + (isIgnoringBatteryOptimizations() ? "已允许" : "未允许") + "\n"
                + "设备能力：" + screenController.sleepCapabilitySummary());
    }

    private String displayState(String state, boolean verified) {
        String label;
        if ("on".equalsIgnoreCase(state)) {
            label = "亮屏";
        } else if ("standby".equalsIgnoreCase(state)) {
            label = "休眠";
        } else if ("blackout".equalsIgnoreCase(state)) {
            label = "黑幕";
        } else {
            label = "未知";
        }
        return label + (verified ? "（已验证）" : "（未验证）");
    }

    private void testConnection() {
        saveConfig(false);
        toast("正在测试连接…");
        new Thread(() -> {
            try {
                ApiClient client = new ApiClient(config);
                JSONObject response = client.testConnection();
                StatusSnapshot snapshot = screenController.observe();
                config.saveStatus("已连接", response.optString("message", "测试连接成功。"), snapshot);
                runOnUiThread(() -> {
                    refreshStatus();
                    toast("测试连接成功");
                });
            } catch (Throwable ex) {
                StatusSnapshot snapshot = screenController.observe();
                config.saveStatus("连接失败", ex.getClass().getSimpleName() + ": " + (ex.getMessage() == null ? "" : ex.getMessage()), snapshot);
                runOnUiThread(() -> {
                    refreshStatus();
                    toast("连接失败，请检查服务器地址和防火墙");
                });
            }
        }, "display-control-test-connection").start();
    }

    private void openLocalBlackout() {
        Intent intent = new Intent(this, BlackoutActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        startActivity(intent);
    }

    private void clearBlackoutAndWake() {
        screenController.closeBlackout();
        screenController.restoreSleepTimeoutFallback();
        Intent intent = new Intent(this, WakeActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        startActivity(intent);
        refreshStatus();
        toast("已解除黑幕并请求亮屏");
    }

    private void testLocalStandby() {
        saveConfig(false);
        toast("正在测试本机休眠…");
        new Thread(() -> {
            CommandResult result = screenController.testStandby();
            config.saveLastResult(result);
            config.saveStatus(result.success ? "本机休眠测试完成" : "本机休眠测试失败", result.message, result.snapshot);
            runOnUiThread(() -> {
                refreshStatus();
                toast(result.success ? "本机休眠测试已执行" : "本机休眠测试失败，请检查权限");
            });
        }, "display-control-test-standby").start();
    }

    private void requestDeviceAdmin() {
        Intent intent = new Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN);
        intent.putExtra(DevicePolicyManager.EXTRA_DEVICE_ADMIN, screenController.adminComponent());
        intent.putExtra(DevicePolicyManager.EXTRA_ADD_EXPLANATION, "启用后，网页“休眠”命令才能调用 Android 系统锁屏。不会重复执行旧命令，现场手动唤醒优先。");
        startActivity(intent);
    }

    private void requestWriteSettings() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            toast("当前系统不需要单独允许系统设置");
            return;
        }
        Intent intent = new Intent(Settings.ACTION_MANAGE_WRITE_SETTINGS);
        intent.setData(Uri.parse("package:" + getPackageName()));
        startActivity(intent);
    }

    private void requestBatteryOptimization() {
        try {
            Intent intent = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS);
            intent.setData(Uri.parse("package:" + getPackageName()));
            startActivity(intent);
        } catch (Throwable ex) {
            startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS));
        }
    }

    private boolean isIgnoringBatteryOptimizations() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            return true;
        }
        PowerManager powerManager = (PowerManager) getSystemService(POWER_SERVICE);
        return powerManager != null && powerManager.isIgnoringBatteryOptimizations(getPackageName());
    }

    private void startServiceCompat(String action) {
        Intent intent = new Intent(this, AgentService.class);
        intent.setAction(action);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !Constants.ACTION_STOP_SERVICE.equals(action)) {
            startForegroundService(intent);
        } else {
            startService(intent);
        }
    }

    private LinearLayout card() {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(20), dp(18), dp(20), dp(18));
        card.setBackground(round(Color.WHITE, Color.rgb(225, 232, 240), 20, 1));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.WRAP_CONTENT);
        params.setMargins(0, 0, 0, dp(14));
        card.setLayoutParams(params);
        return card;
    }

    private TextView sectionTitle(String value) {
        TextView textView = text(value, 17, Color.rgb(15, 23, 42), true);
        textView.setPadding(0, 0, 0, dp(10));
        return textView;
    }

    private EditText input(String hint) {
        EditText editText = new EditText(this);
        editText.setSingleLine(true);
        editText.setHint(hint);
        editText.setTextSize(15);
        editText.setTextColor(Color.rgb(15, 23, 42));
        editText.setHintTextColor(Color.rgb(148, 163, 184));
        editText.setPadding(dp(12), 0, dp(12), 0);
        editText.setBackground(round(Color.WHITE, Color.rgb(203, 213, 225), 10, 1));
        return editText;
    }

    private LinearLayout row(String label, View field) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.VERTICAL);
        row.setPadding(0, 0, 0, dp(10));
        TextView labelView = text(label, 14, Color.rgb(71, 85, 105), true);
        labelView.setPadding(0, 0, 0, dp(5));
        row.addView(labelView);
        row.addView(field, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            dp(46)));
        return row;
    }

    private LinearLayout buttonRow(Button left, Button right) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setPadding(0, 0, 0, dp(10));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(54), 1);
        lp.setMargins(0, 0, dp(8), 0);
        row.addView(left, lp);
        LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(0, dp(54), 1);
        rp.setMargins(dp(8), 0, 0, 0);
        row.addView(right, rp);
        return row;
    }

    private Button blueButton(String text, View.OnClickListener listener) {
        return button(text, Color.WHITE, Color.rgb(37, 99, 235), Color.rgb(37, 99, 235), listener);
    }

    private Button greenButton(String text, View.OnClickListener listener) {
        return button(text, Color.WHITE, Color.rgb(22, 163, 74), Color.rgb(22, 163, 74), listener);
    }

    private Button whiteButton(String text, View.OnClickListener listener) {
        return button(text, Color.rgb(15, 23, 42), Color.WHITE, Color.rgb(203, 213, 225), listener);
    }

    private Button button(String text, int textColor, int fillColor, int strokeColor, View.OnClickListener listener) {
        Button button = new Button(this);
        button.setText(text);
        button.setTextSize(14);
        button.setTypeface(Typeface.DEFAULT_BOLD);
        button.setTextColor(textColor);
        button.setAllCaps(false);
        button.setMaxLines(2);
        button.setPadding(dp(6), 0, dp(6), 0);
        button.setMinHeight(dp(50));
        button.setGravity(Gravity.CENTER);
        button.setBackground(round(fillColor, strokeColor, 12, 1));
        button.setOnClickListener(listener);
        return button;
    }

    private TextView text(String value, int sp, int color, boolean bold) {
        TextView textView = new TextView(this);
        textView.setText(value);
        textView.setTextSize(sp);
        textView.setTextColor(color);
        if (bold) {
            textView.setTypeface(Typeface.DEFAULT_BOLD);
        }
        return textView;
    }

    private GradientDrawable round(int fillColor, int strokeColor, int radiusDp, int strokeDp) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fillColor);
        drawable.setCornerRadius(dp(radiusDp));
        drawable.setStroke(dp(strokeDp), strokeColor);
        return drawable;
    }

    private int contentWidth() {
        float density = getResources().getDisplayMetrics().density;
        int widthDp = (int) (getResources().getDisplayMetrics().widthPixels / density);
        return widthDp >= 920 ? dp(900) : FrameLayout.LayoutParams.MATCH_PARENT;
    }

    private int dp(int value) {
        return (int) (value * getResources().getDisplayMetrics().density + 0.5f);
    }

    private void toast(String value) {
        Toast.makeText(this, value, Toast.LENGTH_SHORT).show();
    }
}
