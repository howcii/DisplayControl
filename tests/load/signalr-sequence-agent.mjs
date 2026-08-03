import fs from "node:fs";

const recordSeparator = String.fromCharCode(30);
const baseUrl = process.env.DISPLAY_CONTROL_URL ?? "http://127.0.0.1:18085";
const deviceId = process.env.SEQUENCE_AGENT_ID ?? "smoke-sequence-001";
const readyFile = process.env.SEQUENCE_AGENT_READY_FILE;
const timeoutMs = Number(process.env.SEQUENCE_AGENT_TIMEOUT_MS ?? 60000);

const expected = [
  // 第一条黑幕命令只返回 executed，不返回 verified，用来防止网页按钮卡在“发送中”。
  { kind: 3, finalStage: 3, state: 3, verified: false, detail: "sequence blackout window opened" },
  { kind: 1, finalStage: 3, state: 1, verified: false, detail: "sequence power on executed" },
  { kind: 2, finalStage: 3, state: 2, verified: false, detail: "sequence standby executed" },
  { kind: 3, finalStage: 4, state: 3, verified: true, detail: "sequence blackout verified again" },
  { kind: 1, finalStage: 3, state: 1, verified: false, detail: "sequence power on executed again" },
];

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
  let commandIndex = 0;
  const receivedKinds = [];

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
    finish({ ok: false, error: "timeout waiting for command sequence", receivedKinds });
  }, timeoutMs);

  const sendInvocation = (target, argument) => {
    socket.send(hubMessage({
      type: 1,
      target,
      arguments: [argument],
    }));
  };

  const register = () => {
    if (registered) return;
    registered = true;
    sendInvocation("Register", {
      deviceId,
      deviceName: "Smoke Sequence Agent",
      agentVersion: "16.5.0-sequence",
      protocolVersion: 3,
      registrationToken: "smoke",
    });
    sendInvocation("Heartbeat", {
      deviceId,
      observedAtUtc: new Date().toISOString(),
      state: 1,
      isStateVerified: true,
      detail: "sequence agent ready",
    });
    writeReadyFile();
  };

  const report = (command, stage, succeeded, detail) => {
    sendInvocation("ReportCommand", {
      commandId: command.commandId,
      deviceId,
      stage,
      occurredAtUtc: new Date().toISOString(),
      succeeded,
      detail,
    });
  };

  const heartbeat = (step) => {
    sendInvocation("Heartbeat", {
      deviceId,
      observedAtUtc: new Date().toISOString(),
      state: step.state,
      isStateVerified: step.verified,
      detail: step.detail,
    });
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
        const step = expected[commandIndex];
        receivedKinds.push(command?.kind);
        if (!step) {
          finish({ ok: false, error: "unexpected extra command", receivedKinds });
          return;
        }
        if (command?.kind !== step.kind) {
          finish({
            ok: false,
            error: `unexpected command kind ${command?.kind}, expected ${step.kind}`,
            receivedKinds,
          });
          return;
        }

        report(command, 2, true, `sequence accepted ${commandIndex + 1}`);
        report(command, 3, true, `sequence executed ${commandIndex + 1}`);
        if (step.finalStage === 4) {
          report(command, 4, true, step.detail);
        }
        heartbeat(step);
        commandIndex += 1;
        if (commandIndex === expected.length) {
          setTimeout(() => finish({ ok: true, receivedKinds }), 500);
        }
      }
    }
  };

  socket.onerror = () => finish({ ok: false, error: "websocket error", receivedKinds });
  socket.onclose = (event) => {
    if (!completed && event.code !== 1000) {
      finish({ ok: false, error: `close ${event.code}`, receivedKinds });
    }
  };
});

console.log(JSON.stringify(result));
process.exit(result.ok ? 0 : 1);
