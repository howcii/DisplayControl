const recordSeparator = String.fromCharCode(30);
const baseUrl = process.env.DISPLAY_CONTROL_URL ?? "http://127.0.0.1:18081";
const agentCount = Number(process.env.AGENT_COUNT ?? 30);
const heartbeatsPerAgent = Number(process.env.HEARTBEATS_PER_AGENT ?? 100);
const heartbeatIntervalMs = Number(process.env.HEARTBEAT_INTERVAL_MS ?? 100);

async function runAgent(index) {
  const negotiate = await fetch(
    `${baseUrl}/hubs/agent/negotiate?negotiateVersion=1`,
    { method: "POST" },
  );
  if (negotiate.status !== 200) {
    return { ok: false, sent: 0, error: `negotiate ${negotiate.status}` };
  }

  const payload = await negotiate.json();
  const websocketUrl = baseUrl
    .replace(/^http:/, "ws:")
    .replace(/^https:/, "wss:");

  return new Promise((resolve) => {
    const socket = new WebSocket(
      `${websocketUrl}/hubs/agent?id=${encodeURIComponent(payload.connectionToken)}`,
    );
    const deviceId = `soak-${String(index).padStart(3, "0")}`;
    let sent = 0;
    let interval;
    let completed = false;

    const finish = (result) => {
      if (completed) return;
      completed = true;
      clearInterval(interval);
      clearTimeout(timeout);
      resolve({ ...result, sent });
    };

    const timeout = setTimeout(() => {
      try {
        socket.close();
      } catch {
        // The result still records the timeout.
      }
      finish({ ok: false, error: "timeout" });
    }, heartbeatsPerAgent * heartbeatIntervalMs + 10_000);

    socket.onopen = () => {
      socket.send(
        JSON.stringify({ protocol: "json", version: 1 }) + recordSeparator,
      );
    };

    socket.onmessage = () => {
      if (interval) return;

      socket.send(
        JSON.stringify({
          type: 1,
          target: "Register",
          arguments: [{
            deviceId,
            deviceName: `Soak ${index}`,
            agentVersion: "16.0.0",
            protocolVersion: 3,
            registrationToken: "test",
          }],
        }) + recordSeparator,
      );

      interval = setInterval(() => {
        sent += 1;
        socket.send(
          JSON.stringify({
            type: 1,
            target: "Heartbeat",
            arguments: [{
              deviceId,
              observedAtUtc: new Date().toISOString(),
              state: sent % 2 ? 1 : 2,
              isStateVerified: true,
              detail: null,
            }],
          }) + recordSeparator,
        );

        if (sent >= heartbeatsPerAgent) {
          clearInterval(interval);
          setTimeout(() => socket.close(1000, "done"), 200);
        }
      }, heartbeatIntervalMs);
    };

    socket.onerror = () => finish({ ok: false, error: "websocket error" });
    socket.onclose = (event) => finish({
      ok: event.code === 1000,
      error: event.code === 1000 ? null : `close ${event.code}`,
    });
  });
}

const startedAt = Date.now();
const results = await Promise.all(
  Array.from({ length: agentCount }, (_, index) => runAgent(index)),
);
const report = {
  test: "signalr-soak",
  agents: results.length,
  passed: results.filter((result) => result.ok).length,
  failed: results.filter((result) => !result.ok),
  heartbeats: results.reduce((total, result) => total + result.sent, 0),
  elapsedMs: Date.now() - startedAt,
};

console.log(JSON.stringify(report));
process.exitCode = report.failed.length === 0 ? 0 : 1;
