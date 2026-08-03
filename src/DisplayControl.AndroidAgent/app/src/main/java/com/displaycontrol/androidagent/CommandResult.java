package com.displaycontrol.androidagent;

import org.json.JSONException;
import org.json.JSONObject;

/** 命令执行结果。字段名称同时兼容旧 V1.5 和新服务器接口。 */
final class CommandResult {
    final String commandId;
    final String commandName;
    final boolean success;
    final String stage;
    final StatusSnapshot snapshot;
    final String message;
    final long createdAtMs;
    boolean posted;

    CommandResult(
        String commandId,
        String commandName,
        boolean success,
        String stage,
        StatusSnapshot snapshot,
        String message) {
        this(commandId, commandName, success, stage, snapshot, message, System.currentTimeMillis(), false);
    }

    private CommandResult(
        String commandId,
        String commandName,
        boolean success,
        String stage,
        StatusSnapshot snapshot,
        String message,
        long createdAtMs,
        boolean posted) {
        this.commandId = commandId;
        this.commandName = commandName;
        this.success = success;
        this.stage = stage;
        this.snapshot = snapshot;
        this.message = message;
        this.createdAtMs = createdAtMs;
        this.posted = posted;
    }

    JSONObject toJson() {
        JSONObject json = new JSONObject();
        try {
            json.put("commandId", commandId);
            json.put("requestId", commandId);
            json.put("command", commandName);
            json.put("commandName", commandName);
            json.put("apiCommand", commandName);
            json.put("success", success);
            json.put("succeeded", success);
            json.put("stage", stage);
            json.put("commandStage", stage);
            json.put("screenStatus", snapshot.state);
            json.put("screenStatusVerified", snapshot.verified);
            json.put("screenStatusDetails", message);
            json.put("statusDetails", message);
            json.put("screenStatusSource", snapshot.source);
            json.put("createdAtMs", createdAtMs);
            json.put("posted", posted);
        } catch (JSONException ignored) {
        }
        return json;
    }

    static CommandResult fromJson(JSONObject json) {
        if (json == null || json.optString("commandId", "").length() == 0) {
            return null;
        }
        StatusSnapshot snapshot = new StatusSnapshot(
            json.optString("screenStatus", "unknown"),
            json.optBoolean("screenStatusVerified", false),
            json.optString("screenStatusDetails", ""),
            json.optString("screenStatusSource", "android-agent"));
        return new CommandResult(
            json.optString("commandId", ""),
            json.optString("commandName", json.optString("command", "")),
            json.optBoolean("success", false),
            json.optString("stage", "executed"),
            snapshot,
            json.optString("screenStatusDetails", ""),
            json.optLong("createdAtMs", System.currentTimeMillis()),
            json.optBoolean("posted", false));
    }
}
