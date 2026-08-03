package com.displaycontrol.androidagent;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.IBinder;
import android.os.SystemClock;

import org.json.JSONObject;

import java.util.ArrayList;

/** 前台服务：1 秒轮询服务器，执行命令，并把真实状态/回执发回网页。 */
public class AgentService extends Service {
    private static final int NOTIFICATION_ID = 160;
    private static final String CHANNEL_ID = "display_control_agent_background_v2";
    private static final String LEGACY_CHANNEL_ID = "display_control_agent";

    private volatile boolean running;
    private Thread worker;
    private ConfigStore config;
    private ApiClient api;
    private ScreenController screenController;
    private CommandJournal journal;

    @Override
    public void onCreate() {
        super.onCreate();
        config = new ConfigStore(this);
        api = new ApiClient(config);
        screenController = new ScreenController(this, config);
        journal = new CommandJournal(config.preferences());
        startForeground(NOTIFICATION_ID, buildNotification());
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? Constants.ACTION_START_SERVICE : intent.getAction();
        if (Constants.ACTION_STOP_SERVICE.equals(action)) {
            stopAgent();
            return START_NOT_STICKY;
        }
        startLoopIfNeeded();
        return START_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onDestroy() {
        stopAgent();
        super.onDestroy();
    }

    private synchronized void startLoopIfNeeded() {
        if (worker != null && worker.isAlive()) {
            return;
        }
        running = true;
        worker = new Thread(this::runLoop, "display-control-agent-loop");
        worker.start();
    }

    private void stopAgent() {
        running = false;
        if (worker != null) {
            worker.interrupt();
            worker = null;
        }
        StatusSnapshot snapshot = screenController == null
            ? new StatusSnapshot("unknown", false, "Agent 服务已停止。", "android-agent")
            : screenController.observe();
        if (config != null) {
            config.saveStatus("已停止", "Agent 服务已停止。", snapshot);
        }
        stopForeground(true);
        stopSelf();
    }

    private void runLoop() {
        while (running) {
            long delayMs = config.getPollMs();
            try {
                flushPendingResults();
                StatusSnapshot snapshot = screenController.observe();
                JSONObject response = api.poll(snapshot);
                config.saveStatus("已连接", response.optString("message", "Android Agent V1.6 已连接。"), snapshot);

                AgentCommand command = AgentCommand.fromPollResponse(response);
                if (command != null) {
                    handleCommand(command);
                    delayMs = 300;
                }
            } catch (Throwable ex) {
                StatusSnapshot snapshot = screenController.observe();
                String detail = ex.getClass().getSimpleName() + ": " + (ex.getMessage() == null ? "" : ex.getMessage());
                config.saveStatus("连接失败", detail, snapshot);
                delayMs = Math.max(1500, config.getPollMs());
            }
            sleepQuietly(delayMs);
        }
    }

    private void handleCommand(AgentCommand command) {
        CommandResult result = journal.find(command.commandId);
        if (result == null) {
            result = screenController.execute(command);
            journal.save(result);
            config.saveLastResult(result);
        }
        postResult(result);
    }

    private void flushPendingResults() {
        ArrayList<CommandResult> pending = journal.pendingPostResults();
        for (CommandResult result : pending) {
            if (!running) {
                return;
            }
            postResult(result);
        }
    }

    private void postResult(CommandResult result) {
        try {
            api.postResult(result);
            result.posted = true;
            journal.markPosted(result.commandId);
            config.saveLastResult(result);
            config.saveStatus("已连接", "命令回执已发送：" + result.commandName, result.snapshot);
        } catch (Throwable ex) {
            config.saveStatus(
                "回执待重试",
                "命令已执行，但回执发送失败，稍后自动重试：" + ex.getClass().getSimpleName() + " " + (ex.getMessage() == null ? "" : ex.getMessage()),
                result.snapshot);
        }
    }

    private void sleepQuietly(long delayMs) {
        try {
            SystemClock.sleep(Math.max(100, delayMs));
        } catch (Throwable ignored) {
        }
    }

    private Notification buildNotification() {
        NotificationManager manager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && manager != null) {
            createQuietNotificationChannel(manager);
        }

        Intent openIntent = new Intent(this, MainActivity.class);
        PendingIntent pendingIntent = PendingIntent.getActivity(
            this,
            0,
            openIntent,
            Build.VERSION.SDK_INT >= Build.VERSION_CODES.M ? PendingIntent.FLAG_IMMUTABLE : 0);

        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
            ? new Notification.Builder(this, CHANNEL_ID)
            : new Notification.Builder(this);
        return builder
            .setSmallIcon(R.drawable.ic_stat_display)
            .setContentTitle("屏控 Agent")
            .setContentText("后台连接服务运行中")
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setLocalOnly(true)
            .setDefaults(0)
            .setPriority(Notification.PRIORITY_MIN)
            .setVisibility(Notification.VISIBILITY_SECRET)
            .setCategory(Notification.CATEGORY_SERVICE)
            .build();
    }

    private void createQuietNotificationChannel(NotificationManager manager) {
        manager.deleteNotificationChannel(LEGACY_CHANNEL_ID);
        NotificationChannel channel = new NotificationChannel(CHANNEL_ID, "屏控后台服务", NotificationManager.IMPORTANCE_MIN);
        channel.setDescription("保持屏控 Android Agent 后台连接，不弹出连接成功提示。");
        channel.enableLights(false);
        channel.enableVibration(false);
        channel.setSound(null, null);
        channel.setShowBadge(false);
        channel.setLockscreenVisibility(Notification.VISIBILITY_SECRET);
        manager.createNotificationChannel(channel);
    }
}
