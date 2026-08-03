# 恢复基线与已知事实

## 从现有成品确认

- Server V10 为 ASP.NET Core 8 自包含 Windows x64 服务。
- 管理后台使用 React、TypeScript 和 Tailwind CSS。
- 服务端使用 SignalR、EF Core 与 SQLite。
- 管理端 Dashboard Hub 路径为 `/hubs/dashboard`。
- 已发现 API：`/api/auth/*`、`/api/screens*`、`/api/command-requests/*`、`/api/commands/{commandId}` 和 `/api/logs`。
- Windows Agent V15 报告 Agent 版本 15.0.0 和协议版本 2。
- 服务器数据位于 `C:\ProgramData\DisplayControlCenter\data`。
- V10 默认监听 HTTP 8080，并使用 LocalSubnet 防火墙限制。

## 尚未确认

- V15 Agent Hub 的准确路径、客户端方法名和消息 JSON 字段。
- V10 SQLite 的表结构、迁移历史和数据保护用途。
- 黑幕、待机、亮屏命令在 Agent 内部的准确实现。
- Android Agent 的管理权限、设备所有者模式和厂商兼容行为。

这些项目必须通过隔离测试环境中的网络抓取、数据库副本检查和行为测试确认，
不能凭说明文档猜测。

## 临时协议决策

- 恢复工程暂定 Agent Hub 为 `/hubs/agent`。
- V15 使用协议版本 2，V16 使用协议版本 3。
- V15 匿名兼容由 `Compatibility:AllowUnauthenticatedV15Agents` 控制。
- 关闭兼容入口前，必须完成真实 V15 Agent 的兼容性测试。
