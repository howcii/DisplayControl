package com.displaycontrol.androidagent;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;

/** 如果用户在面板中开启“开机自启”，设备重启后自动恢复 Agent 服务。 */
public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        ConfigStore config = new ConfigStore(context);
        if (!config.isAutoStartEnabled()) {
            return;
        }
        Intent serviceIntent = new Intent(context, AgentService.class);
        serviceIntent.setAction(Constants.ACTION_START_SERVICE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(serviceIntent);
        } else {
            context.startService(serviceIntent);
        }
    }
}
