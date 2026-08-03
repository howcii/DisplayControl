package com.displaycontrol.androidagent;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/** 简单 HTTP 客户端。只依赖系统库，减少现场安装风险。 */
final class ApiClient {
    private final ConfigStore config;

    ApiClient(ConfigStore config) {
        this.config = config;
    }

    JSONObject poll(StatusSnapshot snapshot) throws IOException, JSONException {
        JSONObject body = baseBody();
        body.put("screenStatus", snapshot.state);
        body.put("screenStatusVerified", snapshot.verified);
        body.put("screenStatusDetails", snapshot.details);
        body.put("screenStatusSource", snapshot.source);
        body.put("observedAt", System.currentTimeMillis());
        return postJson("/api/android-agent/poll", body);
    }

    JSONObject postResult(CommandResult result) throws IOException, JSONException {
        JSONObject body = baseBody();
        JSONObject resultJson = result.toJson();
        IteratorCompat.copy(resultJson, body);
        return postJson("/api/android-agent/result", body);
    }

    JSONObject testConnection() throws IOException, JSONException {
        StatusSnapshot snapshot = new StatusSnapshot("on", true, "Android Agent V1.6 测试连接。", "android-agent-test");
        return poll(snapshot);
    }

    private JSONObject baseBody() throws JSONException {
        JSONObject body = new JSONObject();
        body.put("deviceId", config.getDeviceId());
        body.put("deviceName", config.getDeviceName());
        body.put("agentVersion", Constants.AGENT_VERSION);
        body.put("protocolVersion", Constants.PROTOCOL_VERSION);
        return body;
    }

    private JSONObject postJson(String path, JSONObject body) throws IOException, JSONException {
        URL url = new URL(config.getServerUrl() + path);
        HttpURLConnection connection = (HttpURLConnection) url.openConnection();
        connection.setRequestMethod("POST");
        connection.setConnectTimeout(Constants.HTTP_TIMEOUT_MS);
        connection.setReadTimeout(Constants.HTTP_TIMEOUT_MS);
        connection.setDoOutput(true);
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        connection.setRequestProperty("Accept", "application/json");
        byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
        connection.setFixedLengthStreamingMode(bytes.length);
        try (OutputStream output = connection.getOutputStream()) {
            output.write(bytes);
        }

        int code = connection.getResponseCode();
        InputStream stream = code >= 200 && code < 300 ? connection.getInputStream() : connection.getErrorStream();
        String text = readAll(stream);
        if (code < 200 || code >= 300) {
            throw new IOException("HTTP " + code + ": " + text);
        }
        if (text.trim().length() == 0) {
            return new JSONObject();
        }
        return new JSONObject(text);
    }

    private static String readAll(InputStream stream) throws IOException {
        if (stream == null) {
            return "";
        }
        StringBuilder builder = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                builder.append(line);
            }
        }
        return builder.toString();
    }

    /** JSONObject 没有稳定的拷贝工具，单独封装能让协议字段合并更清楚。 */
    private static final class IteratorCompat {
        static void copy(JSONObject source, JSONObject target) throws JSONException {
            java.util.Iterator<String> keys = source.keys();
            while (keys.hasNext()) {
                String key = keys.next();
                target.put(key, source.opt(key));
            }
        }
    }
}
