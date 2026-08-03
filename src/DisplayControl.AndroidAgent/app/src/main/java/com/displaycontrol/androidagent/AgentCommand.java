package com.displaycontrol.androidagent;

import android.text.TextUtils;

import org.json.JSONObject;

/** 服务器轮询响应中的单条命令。 */
final class AgentCommand {
    final String commandId;
    final String commandName;
    final String mode;

    private AgentCommand(String commandId, String commandName, String mode) {
        this.commandId = commandId;
        this.commandName = commandName;
        this.mode = mode;
    }

    static AgentCommand fromPollResponse(JSONObject response) {
        if (response == null || !response.optBoolean("hasCommand", false)) {
            return null;
        }

        JSONObject commandObject = response.optJSONObject("command");
        JSONObject source = commandObject != null ? commandObject : response;
        String commandId = firstNonEmpty(
            source.optString("requestId", null),
            source.optString("commandId", null),
            response.optString("requestId", null),
            response.optString("commandId", null));
        String commandName = firstNonEmpty(
            source.optString("command", null),
            source.optString("commandName", null),
            source.optString("apiCommand", null),
            response.optString("commandName", null),
            response.optString("apiCommand", null));
        String mode = firstNonEmpty(source.optString("mode", null), response.optString("mode", null), "");

        if (TextUtils.isEmpty(commandId) || TextUtils.isEmpty(commandName)) {
            return null;
        }

        return new AgentCommand(commandId, commandName, mode);
    }

    boolean isTurnOn() {
        return "screen_on".equalsIgnoreCase(commandName) || "on".equalsIgnoreCase(commandName);
    }

    boolean isScreenOff() {
        return "screen_off".equalsIgnoreCase(commandName)
            || "off".equalsIgnoreCase(commandName)
            || "screen_off_standby".equalsIgnoreCase(commandName)
            || "screen_off_blackout".equalsIgnoreCase(commandName);
    }

    boolean isBlackoutMode() {
        return "blackout".equalsIgnoreCase(mode) || "screen_off_blackout".equalsIgnoreCase(commandName);
    }

    boolean isStandbyMode() {
        return "standby".equalsIgnoreCase(mode)
            || "screen_off_standby".equalsIgnoreCase(commandName)
            || (isScreenOff() && !isBlackoutMode());
    }

    String apiCommandForResult() {
        if (isTurnOn()) {
            return "screen_on";
        }
        return isBlackoutMode() ? "screen_off_blackout" : "screen_off_standby";
    }

    private static String firstNonEmpty(String... values) {
        for (String value : values) {
            if (value != null && value.trim().length() > 0) {
                return value.trim();
            }
        }
        return "";
    }
}
