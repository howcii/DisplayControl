# 开发、测试与发布指南

## 1. 开发环境

- 项目根目录：`A:\CodexProjects\DisplayControl`
- 解决方案：`DisplayControl.slnx`
- SDK 版本：.NET SDK 10.0.302
- 本机 SDK：`A:\CodexTools\dotnet\dotnet.exe`
- 目标平台：Windows x64

所有源码、构建缓存、测试数据和发布装配文件都应保留在项目目录或明确的临时目录；
不要把程序本体发布到 `LocalAppData` 或 `ProgramData`。

## 2. 构建

在 PowerShell 中执行：

```powershell
$projectRoot = 'A:\CodexProjects\DisplayControl'
$dotnet = 'A:\CodexTools\dotnet\dotnet.exe'

& $dotnet restore (Join-Path $projectRoot 'DisplayControl.slnx')
& $dotnet build (Join-Path $projectRoot 'DisplayControl.slnx') -c Release --no-restore
```

仓库启用了“警告即错误”。构建必须是 0 个错误、0 个警告，不能通过关闭分析器隐藏问题。

## 3. 自动化回归测试

核心测试是一个无第三方测试框架的控制台运行器，应直接运行：

```powershell
& $dotnet run `
  --project (Join-Path $projectRoot 'tests\DisplayControl.Contracts.Tests\DisplayControl.Contracts.Tests.csproj') `
  -c Release
```

合格条件：进程退出码为 0，输出中的 `failed=0`。

## 4. 隔离启动与冒烟测试

旧服务可能仍占用 8080。开发测试统一使用临时端口，不要停止或覆盖旧服务：

```powershell
$env:ASPNETCORE_URLS = 'http://127.0.0.1:18081'
$env:Storage__DataPath = (Join-Path $projectRoot 'artifacts\smoke-data')

& $dotnet run `
  --project (Join-Path $projectRoot 'src\DisplayControl.Server\DisplayControl.Server.csproj') `
  -c Release `
  --no-build
```

至少验证：

1. `/health` 返回成功。
2. 未登录访问受保护的 `/api/screens` 被拒绝。
3. 登录页能获取验证码，登录后 `/api/auth/session` 保持有效。
4. 退出后受保护 API 重新被拒绝。
5. Windows Agent 能在临时地址注册、发送心跳并自动重连。
6. 单台和批量命令能形成请求编号，命令回执能更新阶段。
7. 重启临时 Server 后，设备资料和操作日志能从测试数据目录恢复。

测试完成后清除本 PowerShell 会话中的临时环境变量：

```powershell
Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
Remove-Item Env:Storage__DataPath -ErrorAction SilentlyContinue
```

## 5. SignalR 压力测试

先在临时端口启动 Server，再运行：

```powershell
$env:DISPLAY_CONTROL_URL = 'http://127.0.0.1:18081'
$env:AGENT_COUNT = '30'
$env:HEARTBEATS_PER_AGENT = '100'
$env:HEARTBEAT_INTERVAL_MS = '100'

& 'C:\Users\ASUS\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  (Join-Path $projectRoot 'tests\load\signalr-soak.mjs')
```

合格条件：输出 JSON 中 `failed` 为空，心跳总数等于
`AGENT_COUNT × HEARTBEATS_PER_AGENT`。

## 6. 发布装配

发布必须从干净的 A 盘 `.workspace\artifacts` 装配目录生成，不直接修改 `releases`
中的二进制文件。

仓库已提供一键装配脚本：

```powershell
& (Join-Path $projectRoot 'scripts\Build-Release.ps1')
```

脚本会生成自包含的 Windows x64 Server、Server Manager 和 Agent，从 Android
源码构建已签名 APK，将文档与可维护源码装入完整包，并在
`.workspace\artifacts\release\packages` 生成 5 个 ZIP 和 `SHA256SUMS.txt`。

完整包固定采用以下结构：

- `01-安装包`：Server、Windows Agent、Android Agent；
- `02-完整源码`：四端源码、共享协议、测试、脚本和诊断工具源码；
- `03-项目文档`：项目说明、部署说明和版本记录；
- `04-测试与质量`：当前测试报告与结构检查结果；
- `05-设计规范`：四端统一 UI 规范。

完整包根目录只允许 `.cmd` 双击入口和 `.txt` 说明文件。`Build-Release.ps1` 会自动
检查必要目录、源码缓存和根目录文件类型，结构不完整时直接停止发布。

推荐顺序：

1. 完成 Release 构建、核心回归、登录冒烟和 SignalR 测试。
2. 运行 `scripts\Build-Release.ps1`。
3. 对刚生成的 Server 文件夹运行发布包冒烟测试：

   ```powershell
   $serverDir = Get-ChildItem (Join-Path $projectRoot '.workspace\artifacts\release\stage') `
     -Directory -Filter 'DisplayControlServer-win-x64-*' |
     Sort-Object Name -Descending |
     Select-Object -First 1
   & (Join-Path $projectRoot 'scripts\Test-Release.ps1') `
     -ServerDirectory $serverDir.FullName
   ```

4. 对文件夹版和 ZIP 解压版分别启动验证，避免只测试源码输出。
5. 将当前发布移入一个明确命名的 `_previous` 快照，再放入新发布。
6. 确认新发布和回滚包都可用后，只保留这一个前版本快照。

发布命名使用构建时间戳：

- `DisplayControlServer-win-x64-YYYYMMDD-HHmm`
- `DisplayControlAgent-win-x64-YYYYMMDD-HHmm`
- `DisplayControlAndroidAgent-v1.6.9-YYYYMMDD-HHmm`
- `DisplayControlProject-YYYYMMDD-HHmm-full`

## 7. 发布验收清单

- Server 管理器能安装、启动、停止和卸载后台服务。
- 服务二进制路径指向当前 Server 包，数据写入当前包的 `data`。
- Server 发布包第一层包含 `DisplayControl.Server.Manager.exe`，Windows Agent 发布包
  第一层包含 `DisplayControl.Agent.exe`；普通用户可直接双击，配置、文档和辅助脚本进入二级目录。
- 完整项目包根目录只保留双击入口和说明，安装包、源码、文档、测试与设计规范均位于
  对应二级目录。
- 从另一 Server 包目录更新服务时，新目录为空则自动迁移旧目录的 `data`。
- 本机和同一局域网内的控制网页可访问。
- 登录、刷新、退出和修改密码流程正确。
- Windows Agent 配置保存后重启仍保留，开机快捷方式指向当前包。
- 所有目标设备逐台验证在线、亮屏、待机、黑幕、恢复和断网重连。
- 网页显示的是 Agent 实测状态，并正确区分未知与未验证状态。
- 操作日志和命令阶段与实际设备行为一致。
- Android APK 版本、应用名、包名和 v1/v2 签名校验通过，并在至少一台真实 Android
  设备上复测。
- 当前包、ZIP 包和 `_previous` 回滚包均能解压启动。

## 8. 回滚

1. 停止当前 V11 服务。
2. 保留当前故障包和 `data` 副本，便于排查。
3. 从 `releases\_previous` 解压前一版本到独立目录。
4. 使用前一版本管理器重新安装/更新服务。
5. 先在本机验证登录和设备列表，再恢复局域网使用。

回滚时不要删除唯一的数据副本，也不要覆盖旧 V10/V15/V1.5 成品。
