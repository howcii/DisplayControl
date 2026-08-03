package com.displaycontrol.androidagent;

import android.app.admin.DevicePolicyManager;
import android.content.ContentResolver;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.PowerManager;
import android.os.SystemClock;
import android.provider.Settings;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.util.ArrayList;

/** 所有屏幕控制集中在这里，避免服务循环里夹杂平台细节。 */
final class ScreenController {
    private final Context appContext;
    private final ConfigStore config;
    private final PowerManager powerManager;
    private final DevicePolicyManager devicePolicyManager;
    private final ComponentName adminComponent;
    private final ContentResolver contentResolver;
    private final PackageManager packageManager;

    ScreenController(Context context, ConfigStore config) {
        this.appContext = context.getApplicationContext();
        this.config = config;
        this.powerManager = (PowerManager) appContext.getSystemService(Context.POWER_SERVICE);
        this.devicePolicyManager = (DevicePolicyManager) appContext.getSystemService(Context.DEVICE_POLICY_SERVICE);
        this.adminComponent = new ComponentName(appContext, DisplayDeviceAdminReceiver.class);
        this.contentResolver = appContext.getContentResolver();
        this.packageManager = appContext.getPackageManager();
    }

    CommandResult execute(AgentCommand command) {
        if (command.isTurnOn()) {
            return turnOn(command);
        }
        if (command.isScreenOff() && command.isBlackoutMode()) {
            return showBlackout(command);
        }
        if (command.isScreenOff() && command.isStandbyMode()) {
            return standby(command);
        }

        StatusSnapshot snapshot = observe();
        return new CommandResult(
            command.commandId,
            command.apiCommandForResult(),
            false,
            "failed",
            snapshot,
            "Android Agent V1.6 不认识该命令：" + command.commandName);
    }

    StatusSnapshot observe() {
        if (config.isBlackoutActive()) {
            return new StatusSnapshot(
                "blackout",
                true,
                "Android Agent V1.6 黑幕遮挡 Activity 已开启。",
                "android-blackout-activity");
        }

        boolean interactive = isInteractive();
        if (interactive) {
            restoreSleepTimeoutFallbackIfNeeded();
        }
        if (!interactive) {
            return new StatusSnapshot(
                "standby",
                true,
                "Android 系统报告屏幕处于非交互状态，按休眠/息屏显示。",
                "android-power-manager");
        }

        return new StatusSnapshot(
            "on",
            true,
            "Android 系统报告屏幕可交互，且未开启黑幕遮挡。",
            "android-power-manager");
    }

    boolean isDeviceAdminActive() {
        return devicePolicyManager != null && devicePolicyManager.isAdminActive(adminComponent);
    }

    boolean canWriteSystemSettings() {
        return Build.VERSION.SDK_INT < Build.VERSION_CODES.M || Settings.System.canWrite(appContext);
    }

    String sleepCapabilitySummary() {
        boolean hasDeviceAdminFeature = packageManager == null
            || packageManager.hasSystemFeature("android.software.device_admin");
        boolean hasSecureLockScreen = packageManager == null
            || packageManager.hasSystemFeature("android.software.secure_lock_screen");
        return "设备管理特性：" + yesNo(hasDeviceAdminFeature)
            + "；安全锁屏特性：" + yesNo(hasSecureLockScreen)
            + "；系统设置权限：" + yesNo(canWriteSystemSettings());
    }

    ComponentName adminComponent() {
        return adminComponent;
    }

    void closeBlackout() {
        config.setBlackoutActive(false);
        Intent closeIntent = new Intent(Constants.ACTION_CLOSE_BLACKOUT);
        closeIntent.setPackage(appContext.getPackageName());
        appContext.sendBroadcast(closeIntent);
    }

    private CommandResult showBlackout(AgentCommand command) {
        try {
            Intent intent = new Intent(appContext, BlackoutActivity.class);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            appContext.startActivity(intent);
            config.setBlackoutActive(true);
            SystemClock.sleep(250);
            StatusSnapshot snapshot = new StatusSnapshot(
                "blackout",
                true,
                "黑幕遮挡已开启。它只是遮挡画面，不锁屏、不反复压制用户手动操作。",
                "android-blackout-activity");
            return new CommandResult(
                command.commandId,
                "screen_off_blackout",
                true,
                "verified",
                snapshot,
                snapshot.details);
        } catch (Throwable ex) {
            StatusSnapshot snapshot = observe();
            return new CommandResult(
                command.commandId,
                "screen_off_blackout",
                false,
                "failed",
                snapshot,
                "黑幕遮挡启动失败：" + ex.getClass().getSimpleName() + " " + safeMessage(ex));
        }
    }

    CommandResult testStandby() {
        return performStandby("local-standby-" + System.currentTimeMillis());
    }

    void restoreSleepTimeoutFallback() {
        restoreSleepTimeoutFallbackIfNeeded();
    }

    private CommandResult standby(AgentCommand command) {
        return performStandby(command.commandId);
    }

    private CommandResult performStandby(String commandId) {
        ArrayList<String> attempts = new ArrayList<>();
        try {
            closeBlackout();
            SystemClock.sleep(150);

            CommandResult result = tryDevicePolicyStandby(commandId, attempts);
            if (result != null) {
                return result;
            }

            result = trySystemSleepKey(commandId, attempts);
            if (result != null) {
                return result;
            }

            result = tryScreenTimeoutStandby(commandId, attempts);
            if (result != null) {
                return result;
            }

            StatusSnapshot snapshot = observe();
            return new CommandResult(
                commandId,
                "screen_off_standby",
                false,
                "failed",
                snapshot,
                "休眠未成功。" + joinAttempts(attempts)
                    + " 请先在电脑上运行安卓包里的“启用安卓休眠权限-ADB.cmd”。若 ADB active-admin 仍失败，说明这台安卓会议屏固件拦截了普通 APK 的系统休眠，需要厂商电源接口、设备所有者模式或继续使用黑幕模式。");
        } catch (Throwable ex) {
            StatusSnapshot snapshot = observe();
            return new CommandResult(
                commandId,
                "screen_off_standby",
                false,
                "failed",
                snapshot,
                "系统休眠执行异常：" + ex.getClass().getSimpleName() + " " + safeMessage(ex));
        }
    }

    private CommandResult tryDevicePolicyStandby(String commandId, ArrayList<String> attempts) {
        if (!isDeviceAdminActive()) {
            attempts.add("设备管理员未启用，跳过标准锁屏");
            return null;
        }

        try {
            devicePolicyManager.lockNow();
            SystemClock.sleep(700);
            StatusSnapshot snapshot = observe();
            boolean verified = "standby".equals(snapshot.state);
            if (verified) {
                return new CommandResult(
                    commandId,
                    "screen_off_standby",
                    true,
                    "verified",
                    snapshot,
                    "系统休眠/锁屏已执行并确认。现场人员手动唤醒后，Agent 不会重复执行旧命令。");
            }
            attempts.add("标准锁屏已调用，但系统仍报告亮屏");
            return null;
        } catch (Throwable ex) {
            attempts.add("标准锁屏失败：" + ex.getClass().getSimpleName() + " " + safeMessage(ex));
            return null;
        }
    }

    private CommandResult trySystemSleepKey(String commandId, ArrayList<String> attempts) {
        int[] keyCodes = new int[] { 223, 276, 26 };
        String[] labels = new String[] { "SLEEP", "SOFT_SLEEP", "POWER" };
        StringBuilder detail = new StringBuilder();
        boolean sentAny = false;

        for (int i = 0; i < keyCodes.length; i++) {
            if (!runInputKeyEvent(keyCodes[i], detail)) {
                continue;
            }
            sentAny = true;
            SystemClock.sleep(1600);
            StatusSnapshot snapshot = observe();
            if ("standby".equals(snapshot.state)) {
                return new CommandResult(
                    commandId,
                    "screen_off_standby",
                    true,
                    "verified",
                    snapshot,
                    "系统 " + labels[i] + " 按键已执行并确认。");
            }
            detail.append(labels[i]).append(" 已发送但仍亮屏; ");
        }

        if (!sentAny) {
            attempts.add("系统睡眠按键不可用：" + trimDetail(detail.toString()));
        } else {
            attempts.add("系统睡眠/电源按键已发送，但系统仍报告亮屏：" + trimDetail(detail.toString()));
        }
        return null;
    }

    private CommandResult tryScreenTimeoutStandby(String commandId, ArrayList<String> attempts) {
        if (!canWriteSystemSettings()) {
            attempts.add("未允许“修改系统设置”，跳过系统超时休眠");
            return null;
        }

        try {
            int originalTimeout = Settings.System.getInt(contentResolver, Settings.System.SCREEN_OFF_TIMEOUT, 30000);
            if (!config.hasSleepTimeoutBackup()) {
                config.saveSleepTimeoutBackup(originalTimeout);
            }
            Settings.System.putInt(contentResolver, Settings.System.SCREEN_OFF_TIMEOUT, 1000);
            SystemClock.sleep(20000);
            StatusSnapshot snapshot = observe();
            if ("standby".equals(snapshot.state)) {
                return new CommandResult(
                    commandId,
                    "screen_off_standby",
                    true,
                    "verified",
                    snapshot,
                    "系统超时休眠已执行并确认。亮屏或现场手动唤醒后会自动恢复原息屏时间。");
            }
            attempts.add("系统超时休眠已设置，但固件未在 20 秒内息屏");
        } catch (Throwable ex) {
            attempts.add("系统超时休眠失败：" + ex.getClass().getSimpleName() + " " + safeMessage(ex));
        }
        restoreSleepTimeoutFallbackIfNeeded();
        return null;
    }

    private CommandResult turnOn(AgentCommand command) {
        closeBlackout();
        restoreSleepTimeoutFallbackIfNeeded();
        try {
            Intent intent = new Intent(appContext, WakeActivity.class);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            appContext.startActivity(intent);
        } catch (Throwable ignored) {
            // 即使后台亮屏 Activity 被系统限制，也继续回读真实状态并给服务器明确回执。
        }

        SystemClock.sleep(1200);
        StatusSnapshot snapshot = observe();
        boolean verified = "on".equals(snapshot.state);
        boolean success = verified;
        return new CommandResult(
            command.commandId,
            "screen_on",
            success,
            verified ? "verified" : "failed",
            snapshot,
            verified
                ? "亮屏/解除黑幕已确认。"
                : "已请求亮屏并解除黑幕，但 Android 系统仍未报告亮屏；可能需要现场人员按电源键或解除系统锁。");
    }

    private boolean isInteractive() {
        if (powerManager == null) {
            return true;
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.KITKAT_WATCH) {
            return powerManager.isInteractive();
        }
        return powerManager.isScreenOn();
    }

    private boolean runInputKeyEvent(int keyCode, StringBuilder detail) {
        Process process = null;
        try {
            process = new ProcessBuilder("/system/bin/input", "keyevent", String.valueOf(keyCode))
                .redirectErrorStream(true)
                .start();
            int exitCode = waitForProcess(process, 1200);
            String output = readProcessOutput(process);
            if (exitCode == 0) {
                return true;
            }
            detail.append("keyevent ").append(keyCode).append(" exit=").append(exitCode).append(" ").append(output).append("; ");
        } catch (Throwable ex) {
            detail.append("keyevent ").append(keyCode).append(" ")
                .append(ex.getClass().getSimpleName()).append(" ")
                .append(safeMessage(ex)).append("; ");
        } finally {
            if (process != null) {
                process.destroy();
            }
        }
        return false;
    }

    private int waitForProcess(Process process, long timeoutMs) {
        long deadline = SystemClock.uptimeMillis() + timeoutMs;
        while (SystemClock.uptimeMillis() < deadline) {
            try {
                return process.exitValue();
            } catch (IllegalThreadStateException ex) {
                SystemClock.sleep(50);
            }
        }
        process.destroy();
        return -1;
    }

    private String readProcessOutput(Process process) {
        StringBuilder output = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(process.getInputStream()))) {
            String line;
            while ((line = reader.readLine()) != null && output.length() < 400) {
                output.append(line).append(' ');
            }
        } catch (Throwable ignored) {
        }
        return output.toString();
    }

    private void restoreSleepTimeoutFallbackIfNeeded() {
        if (!config.hasSleepTimeoutBackup() || !canWriteSystemSettings()) {
            return;
        }
        long startedAt = config.getSleepTimeoutFallbackStartedAt();
        if (startedAt != 0L && System.currentTimeMillis() - startedAt < 1500) {
            return;
        }
        try {
            Settings.System.putInt(contentResolver, Settings.System.SCREEN_OFF_TIMEOUT, config.getSleepTimeoutBackupMs());
        } catch (Throwable ignored) {
        }
        config.clearSleepTimeoutBackup();
    }

    private static String yesNo(boolean value) {
        return value ? "支持" : "不支持";
    }

    private static String joinAttempts(ArrayList<String> attempts) {
        if (attempts.isEmpty()) {
            return "";
        }
        StringBuilder builder = new StringBuilder(" 已尝试：");
        for (int i = 0; i < attempts.size(); i++) {
            if (i > 0) {
                builder.append("；");
            }
            builder.append(attempts.get(i));
        }
        builder.append("。");
        return builder.toString();
    }

    private static String trimDetail(String value) {
        String text = value == null ? "" : value.trim();
        return text.length() == 0 ? "无系统输出" : text;
    }

    private static String safeMessage(Throwable ex) {
        return ex.getMessage() == null ? "" : ex.getMessage();
    }
}
