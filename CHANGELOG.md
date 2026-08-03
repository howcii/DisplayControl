# 版本记录

本文件根据项目内已经验证的发布说明整理。当前工作区尚未建立可追溯的 Git 提交历史，
因此以下内容以 `docs/VERSION_NOTES.md` 和测试报告为事实来源。

## 2026.08.03.1 直接运行版

### 修复与优化

- 控制网页正式限制为单一管理员设备在线；新设备登录后立即废除旧设备会话。
- 旧设备不再只显示笼统的“登录状态已失效”，改为明确提示账号已在另一台设备登录。
- 控制网页每 5 秒轻量检查一次管理会话；控制 API 仍在每次请求时即时拒绝旧会话。
- 验证码增加字符旋转、非等比缩放、基线错位、斜切和轻微波形变形，同时减少干扰线密度以保持人眼可读。
- 验证码字符增加三段分片错位，在保持五位字符可读的前提下提高机器识别难度。
- 删除控制网页、Server 管理器、Windows Agent 与 Android Agent 中重复的开发说明和装饰性副文案；保留状态、错误、权限和操作结果。
- Server 管理器与 Windows Agent 改为自包含单文件 EXE，解压后可从包第一层直接双击运行，无需安装 .NET 运行库。
- Windows Agent 配置统一放在包内 `config/appsettings.json`；Server 数据继续放在包内 `data`。
- Android Agent 更新为 1.6.9，安装文件名固定为 `DisplayControlAndroidAgent-v1.6.9.apk`。
- 新增单一会话替换、会话失效原因、验证码大小写与单次使用回归测试。

### 当前验证

- .NET Release 构建：0 警告、0 错误。
- 核心回归：11/11。
- 发布烟测：39/39，覆盖登录、验证码、单一会话、Android 兼容轮询、SignalR 并发和连续控制。
- Server Manager 与 Windows Agent 单文件 EXE 实际启动 5 秒均保持运行。
- Android APK 元数据正确，v1/v2 签名验证通过。

## 2026.07.30.3 完整项目整理版

### 项目整理

- 将四个正式程序、共享协议、测试、打包模板、文档和设计规范统一纳入完整项目。
- 将 Android RS232 检测版移到 `tools/diagnostics`，不再与正式 Android Agent 混放。
- 完整交付包改为“安装包、完整源码、项目文档、测试与质量、设计规范”五个分区。
- 完整包根目录只保留用户需要双击的 `.cmd` 入口和说明文件。
- 开发缓存、临时 Android 源码和测试输出统一收进 A 盘项目根目录的 `.workspace`。
- 新增完整项目清单、中文 README、版本记录和辅助工具边界说明。

### 版本同步

- 对外版本：2026.07.30.3。
- Server：11.7.0。
- Windows Agent：16.6。
- Android Agent：1.6.8，`versionCode=24`。

### 验证

- .NET Release 构建：0 警告、0 错误。
- 核心回归：9/9。
- 发布烟测：39/39。
- 完整项目结构：12 项必要内容全部存在，源码缓存目录 0 个。
- Android APK：包名、应用名、版本和 v1/v2 签名验证通过。
- 5 个 ZIP 的 SHA256 全部匹配。

## 2026.07.30.2 全端视图修正版

### 修复

- 修正 Server Manager 默认窗口、状态卡、访问地址和操作区域的裁切问题。
- 修正 Windows Agent 配置、操作按钮、说明条和日志区域的显示比例。
- 调整 Android Agent 横屏和大屏布局，保持配置、状态和操作完整可见。

### 版本同步

- Server：11.7.0。
- Windows Agent：16.6，上报版本 16.6.0。
- Android Agent：1.6.7，`versionCode=23`。
- 控制网页、Server Manager、Windows Agent 和 Android Agent 统一使用
  “2026.07.30 全端视图修正版”与运营蓝白控制台视觉。

### 验证

- .NET Release 构建：0 警告、0 错误。
- 核心回归：9/9。
- 发布烟测：39/39。
- Android APK：Release 构建通过，v1/v2 签名验证通过。

## 2026.07.29.5 统一 UI 优化版

- 四端统一为浅蓝背景、白色卡片、蓝色主按钮和清晰状态层级。
- Android Agent 更新为 1.6.6。
- Server Manager 和 Windows Agent 采用可缩放的运维面板布局。

## 更早版本

2026.07.29.4 及以前的逐次修复、兼容范围和已知限制见
[`docs/VERSION_NOTES.md`](docs/VERSION_NOTES.md)。
