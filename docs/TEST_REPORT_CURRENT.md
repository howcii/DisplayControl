# 当前直接运行版测试报告

文档日期：2026-08-03  
测试对象：`DisplayControlProject-*-full.zip` 完整项目包  
对外版本：2026.08.03.1 直接运行版  
兼容版本：Server 11.7.0 + Windows Agent 16.6 + Android Agent V1.6.9

## 测试结论

本次直接运行版已完成源码编译、核心回归、发布烟测、两个 Windows EXE 实际启动、Android APK 构建、APK 签名验证和包内容检查。  
它可以作为当前最新版继续在笔记本、Windows 展示端和 Android 展示端实机测试。

## 本轮重点验证

- Android Agent V1.6 已从源码重新构建，不再使用 V1.5 反编译补丁作为当前安装包。
- Android APK 包名：`com.displaycontrol.androidagent`。
- Android APK 版本：`versionCode=25`，`versionName=1.6.9`。
- Server 管理器和 Windows Agent 均为 win-x64 自包含单文件 EXE，可从各自发布包第一层直接双击运行。
- 四端删除重复的开发说明和装饰性副文案，保留状态、错误、权限和操作结果。
- 验证码使用五位字母数字、大小写不敏感、波形位移和三段分片错位；单一管理设备会话限制已启用。
- 完整 ZIP 根目录只保留双击入口和说明，其余内容按安装包、源码、文档、测试与设计规范分区。
- RS232 检测版已从正式 `src` 移到 `tools/diagnostics`，且不进入正式安装包。
- Android 安装包新增 `启用安卓休眠权限-ADB.cmd`，用于普通系统设置授权无效时通过 ADB 尝试启用设备管理员权限。
- Android 安装包内置最小 ADB 运行文件，现场电脑不需要额外安装 Android SDK。
- Android 休眠链路增强为：设备管理员锁屏、系统睡眠按键、系统设置超时休眠三层尝试。
- Android 休眠按键会连续尝试 `SLEEP`、`SOFT_SLEEP`、`POWER`，前一个按键未让系统回读到休眠时不会提前停止。
- Android 系统超时休眠等待延长到 20 秒，以兼容部分 Android 8 大屏的 15 秒最短息屏限制。
- Android 面板新增“允许系统设置”“本机休眠测试”“恢复息屏时间”和设备能力诊断。
- Android 亮屏/手动唤醒后会恢复原系统息屏时间，避免兼容休眠设置持续压制现场人员操作。
- Android 前台服务通知改为静默后台通知，不再在黑幕模式下反复弹出“服务器连接成功/已连接”提示。
- Android 静默通知通道：`display_control_agent_background_v2`，旧通知通道会在服务启动时删除。
- Android 黑幕遮挡增强为沉浸式全屏，隐藏状态栏和导航栏，并将系统栏背景改为黑色。
- Android 应用新增正式图标，应用名和启动 Activity 标签均为“屏控助手”。
- Android 面板顺序调整为“连接配置”在前、“当前状态”在后；大屏横向显示时使用居中内容列，避免状态长文本被拉成一整屏。
- Android 底部说明已删除 Windows “C 盘”表述。
- 四端 UI 已统一为“运营蓝白控制台”方向：控制网页、Windows Server 管理器、Windows Agent、Android Agent 使用同一组浅蓝背景、白色卡片、蓝色主按钮和清晰状态层级。
- Windows Server 管理器视图已在 2026.07.30.2 中继续调整：默认窗口 `1160×900`，最小窗口 `1080×840`；状态卡、健康检查卡、访问地址、初始密码区和三行服务操作按钮都预留完整高度，避免文字与按钮被裁切。
- Windows Agent 视图已在 2026.07.30.2 中重新调整：默认窗口 `1000×800`，最小窗口 `940×740`；配置表单、操作按钮、说明条和日志区域预留更稳定高度。
- Android APK 已签名，`apksigner verify` 通过 v1/v2 签名校验。
- Android APK 权限包含：网络、前台服务、唤醒锁、开机启动、电池优化设置。
- Android APK 提供设备管理器能力，用于启用真正系统休眠/锁屏。
- Android Agent V1.6.9 面板包含：服务器地址、设备编号、设备名称、轮询间隔、开机自启、当前状态、测试连接、启动/停止服务、本机黑幕测试、解除黑幕/亮屏、系统休眠权限、电池后台设置。
- Android V1.6 轮询协议继续兼容 `/api/android-agent/poll` 和 `/api/android-agent/result`。
- Android V1.6 对同一个 `commandId` 只执行一次；网络抖动时只补发回执，避免旧休眠命令重复压过现场人员手动唤醒。
- Android V1.6 未获得系统锁屏权限时，网页“休眠”会收到明确失败，不再退化成黑幕。
- 发布包和源码包都包含 `src/DisplayControl.AndroidAgent` 源码。
- A 盘发布目录以最新 `current-*` 发布目录为准；本报告不执行服务切换或旧包清理。

## 已验证项目

- Release 编译：0 警告，0 错误。
- 核心回归测试：11/11 通过。
- 发布流程烟测：39/39 通过。
- Android V1.6 Release APK 构建：通过。
- Android V1.6 APK 签名验证：通过。
- Server Manager 单文件 EXE 启动后 5 秒保持运行：通过。
- Windows Agent 单文件 EXE 启动后 5 秒保持运行：通过。
- 完整 ZIP 内容检查：控制网页、Server Manager、Windows Agent、Android APK、完整源码、文档和测试资料均存在。
- 完整项目结构检查：12 项必要内容全部存在，源码缓存目录 0 个，根目录仅含 `.cmd` 和 `.txt` 文件。
- Android APK 元数据：包名 `com.displaycontrol.androidagent`、应用名“屏控助手”、`versionCode=25`、`versionName=1.6.9`。
- Android APK 签名：v1/v2 验证通过。
- SHA256 清单：5 个 ZIP 全部重新计算并匹配。

## 关键产物

- 当前 A 盘发布目录：`A:\CodexProjects\DisplayControl\releases\current-20260803-direct-exe`
- 完整安装包：`A:\CodexProjects\DisplayControl\releases\current-20260803-direct-exe\packages\DisplayControlProject-*-full.zip`
- Server 单独包：`A:\CodexProjects\DisplayControl\releases\current-20260803-direct-exe\packages\DisplayControlServer-win-x64-*.zip`
- Windows Agent 单独包：`A:\CodexProjects\DisplayControl\releases\current-20260803-direct-exe\packages\DisplayControlAgent-win-x64-*.zip`
- Android 单独包：`A:\CodexProjects\DisplayControl\releases\current-20260803-direct-exe\packages\DisplayControlAndroidAgent-v1.6.9-*.zip`
- Android APK：Android 单独包中的 `DisplayControlAndroidAgent-v1.6.9.apk`
- 源码包：`A:\CodexProjects\DisplayControl\releases\current-20260803-direct-exe\packages\DisplayControlSource-*.zip`
- 哈希清单：`A:\CodexProjects\DisplayControl\releases\current-20260803-direct-exe\packages\SHA256SUMS.txt`

## 烟测覆盖

- 健康检查；
- 未登录访问拦截；
- 初始凭据生成；
- 验证码登录；
- 首次改密标记；
- CSRF 令牌返回与缺失拦截；
- 无在线设备时控制命令拒绝；
- 修改密码后退出；
- 新密码登录；
- 单管理端登录限制；
- 版本接口与公开版本接口；
- 设备接口；
- Android HTTP 轮询注册；
- Android 设备在线入库；
- Android 未知状态兜底；
- Android 网页命令下发；
- Android 轮询取得 `requestId + command` 命令；
- Android result 回执接收；
- Android 命令结果收束；
- 重启后 Cookie 会话保持；
- 20 台模拟 Agent 并发心跳，共 600 次心跳；
- 命令请求编号、模拟 Agent 命令回执和命令结果查询；
- `executed` 回执不再导致网页按钮一直等待；
- 设备列表按 Agent 回执即时刷新状态；
- 连续控制：黑幕 → 亮屏 → 休眠 → 黑幕 → 亮屏。

## 仍需实机确认

- Android 真正休眠依赖设备管理器锁屏权限，必须在真实安卓展示屏/会议一体机上启用后验证。
- Android 亮屏受不同厂商系统锁屏策略影响；V1.6 会解除黑幕并请求亮屏，但安全锁屏场景可能仍需要现场人员手动解锁。
- Windows 和网页侧已通过模拟烟测；真实显示屏仍建议逐台做“黑幕、亮屏、休眠、手动唤醒优先”验收。
