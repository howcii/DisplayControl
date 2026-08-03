package com.displaycontrol.androidagent;

import android.content.SharedPreferences;

import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Iterator;

/** 最近命令日志：防止同一个 commandId 因网络抖动被重复执行。 */
final class CommandJournal {
    private static final String KEY_JOURNAL = "commandJournalJson";
    private final SharedPreferences prefs;

    CommandJournal(SharedPreferences prefs) {
        this.prefs = prefs;
    }

    synchronized CommandResult find(String commandId) {
        JSONObject root = load();
        return CommandResult.fromJson(root.optJSONObject(commandId));
    }

    synchronized void save(CommandResult result) {
        JSONObject root = load();
        try {
            root.put(result.commandId, result.toJson());
        } catch (JSONException ignored) {
        }
        trim(root);
        prefs.edit().putString(KEY_JOURNAL, root.toString()).apply();
    }

    synchronized void markPosted(String commandId) {
        JSONObject root = load();
        JSONObject item = root.optJSONObject(commandId);
        if (item != null) {
            try {
                item.put("posted", true);
                root.put(commandId, item);
            } catch (JSONException ignored) {
            }
            prefs.edit().putString(KEY_JOURNAL, root.toString()).apply();
        }
    }

    synchronized ArrayList<CommandResult> pendingPostResults() {
        JSONObject root = load();
        ArrayList<CommandResult> results = new ArrayList<>();
        Iterator<String> keys = root.keys();
        while (keys.hasNext()) {
            CommandResult result = CommandResult.fromJson(root.optJSONObject(keys.next()));
            if (result != null && !result.posted) {
                results.add(result);
            }
        }
        return results;
    }

    private JSONObject load() {
        try {
            JSONObject root = new JSONObject(prefs.getString(KEY_JOURNAL, "{}"));
            removeExpired(root);
            return root;
        } catch (JSONException ex) {
            return new JSONObject();
        }
    }

    private void removeExpired(JSONObject root) {
        long now = System.currentTimeMillis();
        ArrayList<String> expired = new ArrayList<>();
        Iterator<String> keys = root.keys();
        while (keys.hasNext()) {
            String key = keys.next();
            JSONObject item = root.optJSONObject(key);
            if (item == null || now - item.optLong("createdAtMs", 0L) > Constants.COMMAND_JOURNAL_TTL_MS) {
                expired.add(key);
            }
        }
        for (String key : expired) {
            root.remove(key);
        }
    }

    private void trim(JSONObject root) {
        removeExpired(root);
        while (root.length() > Constants.MAX_JOURNAL_ITEMS) {
            String oldestKey = null;
            long oldest = Long.MAX_VALUE;
            Iterator<String> keys = root.keys();
            while (keys.hasNext()) {
                String key = keys.next();
                JSONObject item = root.optJSONObject(key);
                long createdAt = item == null ? 0L : item.optLong("createdAtMs", 0L);
                if (createdAt < oldest) {
                    oldest = createdAt;
                    oldestKey = key;
                }
            }
            if (oldestKey == null) {
                return;
            }
            root.remove(oldestKey);
        }
    }
}
