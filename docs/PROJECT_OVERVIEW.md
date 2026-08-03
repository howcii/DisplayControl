# 展示屏控制系统项目介绍

## 业务背景

项目用于多台展示显示器和会议一体机，由管理员通过一个网页统一查看连接状态，
并执行亮屏、待机或黑幕等控制。系统的设计重点是操作简单、状态可信、断线后能够
自动恢复，同时避免在系统盘散落程序文件。

## 系统组成

| 组件 | 当前版本 | 部署位置 | 主要职责 |
| --- | --- | --- | --- |
| Server | 11.7.0 | 管理员电脑上的 Server 包目录 | 提供网页、登录、设备登记、命令下发、状态汇总和日志 |
| Server Manager | 2026.08.03.1 | Server 包目录 | 安装、启动、停止和卸载 Windows 服务，打开控制网页 |
| Windows Agent | 16.6 | Windows 显示设备的 Agent 包目录 | 连接 Server、执行控制命令、观测屏幕状态并上报 |
| Android Agent | 1.6.9 | Android 显示设备 | 通过 HTTP 轮询连接 Server，执行黑幕、亮屏和系统休眠命令，并上报屏幕状态 |
| Web 控制台 | 随 Server 当前发布版 | 由 Server 的 `wwwroot` 提供 | 登录、查看设备与状态、单台/批量控制、查看操作日志 |

## 控制流程

1. Windows Agent 通过 SignalR 连接 `/hubs/agent`；Android Agent 使用 HTTP 轮询接口。两端都会上报设备编号、名称和版本。
2. Agent 按配置周期发送心跳、屏幕状态和命令执行结果。
3. 管理员登录网页后选择一台或多台设备并发出命令。
4. Server 将命令发送到对应设备连接，并分别记录已发送、已接收、已执行、已验证或失败。
5. Agent 执行命令、再次观测状态并回传结果；网页通过 `/hubs/dashboard` 接收实时更新。

## 状态与可靠性原则

- “在线”只表示当前 Agent 连接有效，不代表屏幕一定处于亮屏状态。
- “已验证”必须来自 Agent 的独立观测，不能只根据最后一次按钮点击推断。
- 同一设备重新连接后，旧连接断开不能把新连接错误标记为离线。
- 设备资料和操作日志使用临时文件替换方式写入，降低写入中断造成文件损坏的概率。
- Server 数据默认保存在 Server 包的 `data` 目录。
- Windows Agent 配置保存在 Agent 包的 `config/appsettings.json`。
- 程序本体不复制到 `LocalAppData` 或 `ProgramData`；开机启动只创建指向当前 Agent 包的快捷方式。

## 版本与兼容范围

- 内部 Windows 服务名继续使用 `DisplayControlCenterV11` 以兼容升级；对外发布包、
  网页和管理器使用日期/小版本号标识最新版。
- 协议 3 是当前协议；协议 2 仅作为 Windows Agent V15 的迁移兼容入口。
- Android V1.6 已恢复源码并可重新构建；旧 V1.5 成品仅保留为历史追溯与回滚参考。
- 旧版 Server V10、Windows Agent V15 和 Android Agent V1.5 成品只作为
  对照与回滚依据，不应被新版本覆盖。

## 源码与成品位置

- 可维护源码：`src`
- 自动化与压力测试：`tests`
- 安装辅助文件模板：`packaging`
- 开发文档：`docs`
- 本地发布成品：`releases`（不提交 Git；正式安装包通过 GitHub Releases 分发）

具体文件职责见 [目录与文件职责](PROJECT_STRUCTURE.md)。
