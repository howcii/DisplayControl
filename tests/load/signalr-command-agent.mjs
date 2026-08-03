import fs from "node:fs";

const recordSeparator = String.fromCharCode(30);
const baseUrl = process.env.DISPLAY_CONTROL_URL ?? "http://127.0.0.1:18085";
const deviceId = process.env.COMMAND_AGENT_ID ?? "smoke-command-001";
const readyFile = process.env.COMMAND_AGENT_READY_FILE;
const timeoutMs = Number(process.env.COMMAND_AGENT_TIMEOUT_MS ?? 30000);
const initialState = Number(process.env.COMMAND_AGENT_INITIAL_STATE ?? 1);
const initialVerified = parseBoolean(process.env.COMMAND_AGENT_INITIAL_VERIFIED, true);
const finalStage = Number(process.env.COMMAND_AGENT_FINAL_STAGE ?? 4);
const finalState = Number(process.env.COMMAND_AGENT_FINAL_STATE ?? 1);
const finalVerified = parseBoolean(process.env.COMMAND_AGENT_FINAL_VERIFIED, true);
const finalDetail = process.env.COMMAND_AGENT_FINAL_DETAIL ?? "smoke command verified";

function parseBoolean(value, fallback) {
  if (value === undefined) return fallback;
  return ["1", "true", "yes", "y"].includes(String(value).toLowerCase());
}

function hubMessage(message) {
  return JSON.stringify(message) + recordSeparator;
}

function writeReadyFile() {
  if (!readyFile) return;
  fs.writeFileSync(readyFile, new Date().toISOString(), "utf8");
}

const negotiate = await fetch(
  `${baseUrl}/hubs/agent/negotiate?negotiateVersion=1`,
  { method: "POST" },
);
if (negotiate.status !== 200) {
  console.log(JSON.stringify({ ok: false, error: `negotiate ${negotiate.status}` }));
  process.exit(1);
}

const payload = await negotiate.json();
const websocketUrl = baseUrl
  .replace(/^http:/, "ws:")
  .replace(/^https:/, "wss:");

const result = await new Promise((resolve) => {
  const socket = new WebSocket(
    `${websocketUrl}/hubs/agent?id=${encodeURIComponent(payload.connectionToken)}`,
  );
  let completed = false;
  let registered = false;

  const finish = (value) => {
    if (completed) return;
    completed = true;
    clearTimeout(timeout);
    try {
      socket.close(1000, "done");
    } catch {
      // Ignore close races during process shutdown.
    }
    resolve(value);
  };

  const timeout = setTimeout(() => {
    finish({ ok: false, error: "timeout waiting for ExecuteCommand" });
  }, timeoutMs);

  const register = () => {
    if (registered) return;
    registered = true;
    socket.send(hubMessage({
      type: 1,
      target: "Register",
      arguments: [{
        deviceId,
        deviceName: "Smoke Command Agent",
        agentVersion: "16.0.0-smoke",
        protocolVersion: 3,
        registrationToken: "smoke",
      }],
    }));
    socket.send(hubMessage({
      type: 1,
      target: "Heartbeat",
      arguments: [{
        deviceId,
        observedAtUtc: new Date().toISOString(),
        state: initialState,
        isStateVerified: initialVerified,
        detail: "smoke agent ready",
      }],
    }));
    writeReadyFile();
  };

  const report = (command, stage, succeeded, detail) => {
    socket.send(hubMessage({
      type: 1,
      target: "ReportCommand",
      arguments: [{
        commandId: command.commandId,
        deviceId,
        stage,
        occurredAtUtc: new Date().toISOString(),
        succeeded,
        detail,
      }],
    }));
  };

  socket.onopen = () => {
    socket.send(hubMessage({ protocol: "json", version: 1 }));
  };

  socket.onmessage = (event) => {
    for (const frame of String(event.data).split(recordSeparator)) {
      if (!frame) continue;
      if (frame === "{}") {
        register();
        continue;
      }

      let message;
      try {
        message = JSON.parse(frame);
      } catch {
        continue;
      }

      if (message.type === 1 && message.target === "ExecuteCommand") {
        const command = message.arguments?.[0];
        report(command, 2, true, "smoke accepted");
        report(command, 3, true, "smoke executed");
        if (finalStage === 4) {
          report(command, 4, true, "smoke verified");
        } else if (finalStage === 5) {
          report(command, 5, false, "smoke failed");
        } else if (finalStage === 6) {
          report(command, 6, false, "smoke timed out");
        }
        socket.send(hubMessage({
          type: 1,
          target: "Heartbeat",
          arguments: [{
            deviceId,
            observedAtUtc: new Date().toISOString(),
            state: finalState,
            isStateVerified: finalVerified,
            detail: finalDetail,
          }],
        }));
        setTimeout(() => {
          finish({ ok: true, commandId: command.commandId, finalStage });
        }, 500);
      }
    }
  };

  socket.onerror = () => finish({ ok: false, error: "websocket error" });
  socket.onclose = (event) => {
    if (!completed && event.code !== 1000) {
      finish({ ok: false, error: `close ${event.code}` });
    }
  };
});

console.log(JSON.stringify(result));
process.exit(result.ok ? 0 : 1);
