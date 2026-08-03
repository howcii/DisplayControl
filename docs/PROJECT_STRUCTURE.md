# 项目目录与文件职责

本说明覆盖需要人工维护的源码、配置、脚本和文档。`releases` 中的 `.dll`、
`.pdb`、运行时文件等均由构建或装配过程生成，不逐一作为源码维护。

## 根目录

| 路径 | 职责 | 是否手工维护 |
| --- | --- | --- |
| `DisplayControl.slnx` | .NET 解决方案入口，登记 4 个正式项目和测试项目 | 是 |
| `Directory.Build.props` | 统一启用 Nullable、推荐分析器、确定性构建和警告即错误 | 是 |
| `global.json` | 固定 .NET SDK 10.0.302，并允许同补丁版本前滚 | 是 |
| `README.md` | 项目入口、当前版本和常用命令 | 是 |
| `AGENTS.md` | 恢复工程的开发边界和兼容性约束 | 是 |
| `.gitignore` | 排除编译输出、本机配置、数据和装配目录 | 是 |
| `src` | 正式源码 | 是 |
| `tests` | 自动化测试与压力测试 | 是 |
| `packaging` | 发布包需复制的安装说明和辅助脚本模板 | 是 |
| `scripts` | 一键发布构建与发布包冒烟测试 | 是 |
| `design-demos` | 已确认 UI 方向的预览图和高保真原型 | 是 |
| `design-system` | 网页与桌面端共用的视觉和交互规范 | 是 |
| `tools` | 独立诊断工具；不属于四端正式程序 | 是 |
| `third_party` | 已核验来源与哈希的兼容成品 | 仅在来源核验后更新 |
| `docs` | 项目说明、规范、版本记录与测试报告 | 是 |
| `releases` | 当前发布成品和一个前版本备份 | 否，由发布流程生成 |
| `.workspace` | A 盘构建缓存、装配目录、临时 Android 源码和测试输出 | 否，可重新生成 |

## `src` 源码

`src` 只包含正式系统源码。Android RS232 检测版位于
`tools/diagnostics/DisplayControl.AndroidSerialProbe`，不会混入正式安装包。

### `DisplayControl.Contracts`

| 文件 | 职责 |
| --- | --- |
| `DisplayControl.Contracts.csproj` | Server 与 Agent 共用的协议程序集 |
| `Protocol.cs` | 协议版本、屏幕状态、命令类型、命令阶段和 SignalR 消息契约 |

### `DisplayControl.Server`

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 服务启动、依赖注入、中间件、登录 API、设备 API、SignalR 路由和静态网页入口 |
| `AuthModels.cs` | 登录、验证码、会话和修改密码的请求/响应模型 |
| `DeviceApiModels.cs` | 网页设备视图、批量命令、执行结果和操作日志模型及映射 |
| `Hubs/AgentHub.cs` | Agent 注册、心跳、命令回执和断开连接处理 |
| `Hubs/DashboardHub.cs` | 已登录网页的实时状态通道 |
| `Services/StoragePaths.cs` | 统一解析 Server 数据目录 |
| `Services/DeviceRegistry.cs` | 设备连接、心跳、离线状态、名称备注和设备持久化 |
| `Services/CommandCoordinator.cs` | 命令分发、阶段更新、网页请求聚合和结果查询 |
| `Services/CommandMaintenanceService.cs` | 定时将未回执命令收束为超时，并清理旧命令状态 |
| `Services/OperationLogStore.cs` | 操作日志读取、限量保留与原子保存 |
| `Services/CaptchaStore.cs` | 验证码创建、过期和一次性校验 |
| `Services/AdminCredentialStore.cs` | 管理员密码初始化、验证和修改 |
| `appsettings.json` | 监听地址、数据路径、兼容入口、限流与日志配置 |
| `appsettings.Development.json` | 仅开发环境覆盖配置 |
| `wwwroot/index.html` | Web 控制台入口及其打包资源 |
| `Properties/launchSettings.json` | 本地开发启动配置 |

### `DisplayControl.Server.Manager`

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | Windows 管理器进程入口 |
| `MainForm.cs` | 服务状态、安装/更新、启停、网页与防火墙等管理操作和界面 |
| `DisplayControl.Server.Manager.csproj` | WinForms 管理器项目配置 |

### `DisplayControl.Agent`

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | Windows Agent 进程入口 |
| `Form1.cs` | 连接管理、命令执行、屏幕状态观测、开机启动和主界面逻辑 |
| `Form1.Designer.cs` | WinForms 设计器生成的控件声明；不要手工堆积业务逻辑 |
| `BlackOverlayForm.cs` | 各显示器上的全黑顶层窗口与紧急退出机制 |
| `AgentSettings.cs` | 当前目录配置文件的读取、默认值、校验和保存 |
| `appsettings.json` | Server 地址、设备身份、心跳和屏幕控制参数 |
| `DisplayControl.Agent.csproj` | WinForms Agent 项目及 SignalR 客户端依赖 |

网页源码当前以可维护的单文件形式放在
`src/DisplayControl.Server/wwwroot/index.html`，不再保留空的
`src/DisplayControl.Web` 占位目录。

## `tests` 测试

| 文件 | 职责 |
| --- | --- |
| `DisplayControl.Contracts.Tests/Program.cs` | 无第三方测试框架的核心协议与设备注册回归测试运行器 |
| `DisplayControl.Contracts.Tests/DisplayControl.Contracts.Tests.csproj` | 测试运行器项目配置和项目引用 |
| `load/signalr-soak.mjs` | 使用多个模拟 Agent 进行 SignalR 连接与心跳压力测试 |
| `load/signalr-command-agent.mjs` | 模拟单台 Agent 接收网页命令并回传 accepted/executed/verified 回执 |

## `packaging` 打包模板

| 目录 | 内容 |
| --- | --- |
| `packaging/full` | 完整包根目录的安装说明、三个双击入口及位置选择工具 |
| `packaging/server` | Server 管理器入口、服务安装/卸载、临时启动、防火墙和说明文件 |
| `packaging/agent` | Windows Agent 启动、设置/取消开机启动和说明文件 |
| `packaging/android` | Android Agent V1.6 APK 的安装说明 |

完整包根目录保留 `.cmd` 快捷入口；独立 Server 与 Windows Agent 包的正式入口是
第一层的单文件 `.exe`。`.ps1` 仅作为程序或快捷入口调用的辅助脚本，普通用户不需要打开。

发布后的 Server 包第一层放 `DisplayControl.Server.Manager.exe`，`app` 保存后台服务器和网页，
`data` 保存运行数据，`docs` 和 `tools` 保存说明与辅助脚本。Windows Agent 包第一层放
`DisplayControl.Agent.exe`，`config` 保存配置，`docs` 和 `tools` 保存说明与辅助脚本。

## `scripts`、`tools` 与 `third_party`

| 路径 | 职责 |
| --- | --- |
| `scripts/Build-Release.ps1` | 自包含发布 Server/Manager/Windows Agent，构建 Android Agent V1.6，装入文档和源码，并生成 ZIP 与 SHA256 清单 |
| `scripts/Test-Release.ps1` | 在独立端口验证健康检查、登录、验证码、CSRF、改密、Cookie 重启保持和 SignalR 并发 |
| `tools/diagnostics` | RS232 等现场兼容性检测工具源码，独立构建、独立交付 |
| `third_party/android` | 原 Android Agent V1.5 APK、固定 SHA256 和来源说明 |

这两个 `.ps1` 是开发发布工具，不是普通用户入口。最终用户可以使用完整包根目录的
快捷入口，也可以进入独立安装包直接双击对应 EXE。

## `releases` 发布成品

| 路径模式 | 含义 |
| --- | --- |
| `DisplayControlServer-win-x64-YYYYMMDD-HHmm` / `.zip` | Server 可部署目录和压缩包 |
| `DisplayControlAgent-win-x64-YYYYMMDD-HHmm` / `.zip` | Windows Agent 可部署目录和压缩包 |
| `DisplayControlAndroidAgent-v1.6.9-YYYYMMDD-HHmm` / `.zip` | Android Agent V1.6.9 APK 安装目录和压缩包 |
| `DisplayControlProject-YYYYMMDD-HHmm-full` / `.zip` | 以安装包、完整源码、文档、测试报告和设计规范分层组织的完整交付包 |
| `_previous/<一次版本快照>` | 仅保留的前一版本，用于快速回滚 |

发布目录中的第三方 .NET 运行时、程序集、调试符号和静态网页资源不可作为源码
直接修改；修改应发生在 `src` 或 `packaging` 后重新构建。

## 数据与本机生成文件

- Server 运行数据：发布目录下的 `data`。
- Windows Agent 配置：Agent 发布目录下的 `config\appsettings.json`。
- 编译缓存：各项目的 `bin`、`obj`。
- 发布装配缓存：A 盘项目目录下的 `.workspace\artifacts`。
- 冒烟测试记录：A 盘项目目录下的 `.workspace\output`。

上述运行数据和缓存不提交为源码，也不能混入下一版本的空白安装包。
