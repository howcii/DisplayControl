package com.displaycontrol.androidagent;

import android.content.Context;
import android.content.SharedPreferences;
import android.provider.Settings;
import android.text.TextUtils;

import org.json.JSONException;
import org.json.JSONObject;

/** 只保存现场需要的少量配置，避免 Agent 行为变复杂。 */
final class ConfigStore {
    private static final String PREFS = "display_control_agent";
    private static final String KEY_SERVER_URL = "serverUrl";
    private static final String KEY_DEVICE_ID = "deviceId";
    private static final String KEY_DEVICE_NAME = "deviceName";
    private static final String KEY_POLL_MS = "pollMs";
    private static final String KEY_AUTO_START = "autoStart";
    private static final String KEY_BLACKOUT_ACTIVE = "blackoutActive";
    private static final String KEY_LAST_STATUS_JSON = "lastStatusJson";
    private static final String KEY_LAST_RESULT_JSON = "lastResultJson";
    private static final String KEY_SLEEP_TIMEOUT_BACKUP_MS = "sleepTimeoutBackupMs";
    private static final String KEY_SLEEP_TIMEOUT_FALLBACK_STARTED_AT = "sleepTimeoutFallbackStartedAt";

    private final Context appContext;
    private final SharedPreferences prefs;

    ConfigStore(Context context) {
        this.appContext = context.getApplicationContext();
        this.prefs = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        ensureDefaults();
    }

    String getServerUrl() {
        return prefs.getString(KEY_SERVER_URL, "http://192.168.1.100:8080");
    }

    String getDeviceId() {
        return prefs.getString(KEY_DEVICE_ID, defaultDeviceId());
    }

    String getDeviceName() {
        return prefs.getString(KEY_DEVICE_NAME, "Android展示屏");
    }

    int getPollMs() {
        return Math.max(500, prefs.getInt(KEY_POLL_MS, Constants.DEFAULT_POLL_MS));
    }

    boolean isAutoStartEnabled() {
        return prefs.getBoolean(KEY_AUTO_START, false);
    }

    boolean isBlackoutActive() {
        return prefs.getBoolean(KEY_BLACKOUT_ACTIVE, false);
    }

    void setBlackoutActive(boolean active) {
        prefs.edit().putBoolean(KEY_BLACKOUT_ACTIVE, active).apply();
        notifyUi();
    }

    boolean hasSleepTimeoutBackup() {
        return prefs.contains(KEY_SLEEP_TIMEOUT_BACKUP_MS);
    }

    int getSleepTimeoutBackupMs() {
        return prefs.getInt(KEY_SLEEP_TIMEOUT_BACKUP_MS, 30000);
    }

    long getSleepTimeoutFallbackStartedAt() {
        return prefs.getLong(KEY_SLEEP_TIMEOUT_FALLBACK_STARTED_AT, 0L);
    }

    void saveSleepTimeoutBackup(int timeoutMs) {
        prefs.edit()
            .putInt(KEY_SLEEP_TIMEOUT_BACKUP_MS, timeoutMs)
            .putLong(KEY_SLEEP_TIMEOUT_FALLBACK_STARTED_AT, System.currentTimeMillis())
            .apply();
        notifyUi();
    }

    void clearSleepTimeoutBackup() {
        prefs.edit()
            .remove(KEY_SLEEP_TIMEOUT_BACKUP_MS)
            .remove(KEY_SLEEP_TIMEOUT_FALLBACK_STARTED_AT)
            .apply();
        notifyUi();
    }

    void save(String serverUrl, String deviceId, String deviceName, int pollMs, boolean autoStart) {
        prefs.edit()
            .putString(KEY_SERVER_URL, normalizeServerUrl(serverUrl))
            .putString(KEY_DEVICE_ID, sanitizeDeviceId(deviceId))
            .putString(KEY_DEVICE_NAME, sanitizeDeviceName(deviceName))
            .putInt(KEY_POLL_MS, Math.max(500, pollMs))
            .putBoolean(KEY_AUTO_START, autoStart)
            .apply();
        notifyUi();
    }

    void saveStatus(String connectionState, String detail, StatusSnapshot snapshot) {
        JSONObject json = new JSONObject();
        try {
            json.put("connectionState", connectionState);
            json.put("detail", detail == null ? "" : detail);
            json.put("screenStatus", snapshot.state);
            json.put("screenStatusVerified", snapshot.verified);
            json.put("screenStatusDetails", snapshot.details);
            json.put("screenStatusSource", snapshot.source);
            json.put("updatedAt", System.currentTimeMillis());
        } catch (JSONException ignored) {
            // JSONObject with primitive values should not fail; keep Agent running if it ever does.
        }
        prefs.edit().putString(KEY_LAST_STATUS_JSON, json.toString()).apply();
        notifyUi();
    }

    JSONObject getLastStatusJson() {
        try {
            return new JSONObject(prefs.getString(KEY_LAST_STATUS_JSON, "{}"));
        } catch (JSONException ex) {
            return new JSONObject();
        }
    }

    void saveLastResult(CommandResult result) {
        prefs.edit().putString(KEY_LAST_RESULT_JSON, result.toJson().toString()).apply();
        notifyUi();
    }

    JSONObject getLastResultJson() {
        try {
            return new JSONObject(prefs.getString(KEY_LAST_RESULT_JSON, "{}"));
        } catch (JSONException ex) {
            return new JSONObject();
        }
    }

    SharedPreferences preferences() {
        return prefs;
    }

    private void ensureDefaults() {
        if (!prefs.contains(KEY_DEVICE_ID)) {
            prefs.edit()
                .putString(KEY_DEVICE_ID, defaultDeviceId())
                .putString(KEY_DEVICE_NAME, "Android展示屏")
                .putString(KEY_SERVER_URL, "http://192.168.1.100:8080")
                .putInt(KEY_POLL_MS, Constants.DEFAULT_POLL_MS)
                .apply();
        }
    }

    private String defaultDeviceId() {
        String androidId = Settings.Secure.getString(appContext.getContentResolver(), Settings.Secure.ANDROID_ID);
        if (TextUtils.isEmpty(androidId)) {
            androidId = Long.toHexString(System.currentTimeMillis());
        }
        String suffix = androidId.length() > 12 ? androidId.substring(androidId.length() - 12) : androidId;
        return "android-agent-" + suffix;
    }

    private static String normalizeServerUrl(String value) {
        String text = value == null ? "" : value.trim();
        if (text.length() == 0) {
            return "http://192.168.1.100:8080";
        }
        if (!text.startsWith("http://") && !text.startsWith("https://")) {
            text = "http://" + text;
        }
        while (text.endsWith("/")) {
            text = text.substring(0, text.length() - 1);
        }
        return text;
    }

    private static String sanitizeDeviceId(String value) {
        String text = value == null ? "" : value.trim();
        if (text.length() == 0) {
            return "android-agent-" + Long.toHexString(System.currentTimeMillis());
        }
        return text.replaceAll("\\s+", "-");
    }

    private static String sanitizeDeviceName(String value) {
        String text = value == null ? "" : value.trim();
        return text.length() == 0 ? "Android展示屏" : text;
    }

    private void notifyUi() {
        appContext.sendBroadcast(new android.content.Intent(Constants.ACTION_STATUS_CHANGED).setPackage(appContext.getPackageName()));
    }
}
