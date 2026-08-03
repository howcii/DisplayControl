# 测试报告 2026-07-27

版本组合：Server V11 + Windows Agent V16 + Android Agent V1.5 兼容包  
测试包：`DisplayControlProject-v11-v16-full.zip`  
测试端口：`http://127.0.0.1:18085`  
测试数据目录：`output/smoke/<runId>/data`

## 结论

本轮自动化构建、核心回归测试、发布包烟雾测试均通过。发布包可用于下一步在目标显示器/会议一体机上做实机验收。

## 已通过的自动化测试

- Release 编译：5 个项目均成功生成，0 警告，0 错误。
- 核心回归测试：9/9 通过。
- 发布包烟雾测试：22/22 通过。
- 登录链路：字母+数字验证码、验证码大小写不敏感、初始密码登录、首次改密、新密码登录通过。
- 初始密码策略：该历史版本生成便于现场输入的临时密码；测试所用具体凭据已从公开文档中脱敏，
  均可正常登录并进入首次改密界面。
- 旧数据升级登录复测：复制当前 8080 所用 `current-20260727-1200` 的 `data` 目录后，用新程序在
  `http://127.0.0.1:18103/` 启动；服务自动把旧 24 位临时密码刷新为新的短临时密码，
  Chrome 输入 admin、该初始密码和页面验证码后，`/api/auth/login` 返回 200，页面进入首次改密界面。
- 登录前端：已改为自定义中文校验，避免浏览器原生 pattern 校验导致“点击登录没反应”的观感。
- 真实浏览器点击测试：在 Chrome 中打开 `http://127.0.0.1:18101/`，输入 admin、临时密码和页面验证码后
  点击“登录屏控中心”，`/api/auth/login` 返回 200，页面进入“首次登录修改管理员密码”，控制台错误 0。
- 登录失败路径：故意输入错误验证码时 `/api/auth/login` 返回 400，页面保留“验证码错误或已过期，
  请刷新验证码后重试。”提示，并自动刷新验证码，不再表现为点击后无反馈。
- 真实浏览器复测：在 Chrome 中打开 `http://127.0.0.1:18102/`，输入 admin、简单临时密码和页面验证码后
  点击“登录屏控中心”，`/api/auth/login` 返回 200，页面进入首次改密界面；截图保存在
  `output/manual-login-20260727-readable/login-after-click-correct-state.png`。
- 初始凭据自动修复：故意把 `initial-admin-credentials.txt` 改成无效测试值后重启测试服务，
  Server 自动重建匹配的初始凭据文件；使用重建后的密码登录返回 200。
- 验证码视觉检查：生成的 SVG 包含字母+数字、大号清晰字符、轻微旋转、浅色边框和 10 个低透明度背景点；
  样张保存在 `output/manual-login-20260727-simple-currentdata/login-before-click-simple-final.png`。
- 控制网页缓存：HTML 返回 `Cache-Control: no-store, no-cache, max-age=0, must-revalidate`，
  升级后浏览器不会继续沿用旧登录脚本。
- 临时启动稳定性：修复普通用户启动 Server 时 Windows Event Log 无权限导致的崩溃，当前测试服务
  已正常监听 `http://127.0.0.1:18101`。
- 会话链路：Cookie 会话、改密后旧会话失效、服务重启后会话保持通过。
- 单管理端限制：第二个网页登录成功后，第一个网页登录自动失效。
- 安全链路：未登录访问返回 401，缺少 CSRF 的状态变更请求被拒绝。
- 命令链路：无在线设备返回 409；模拟 Agent 可收到网页命令并回传 verified；命令请求结果查询通过。
- SignalR 稳定性：20 台模拟 Agent，600 次心跳，失败 0 台；服务端记录 20 台模拟设备。
- 发布包内容：完整包包含 Server、Server Manager、Windows Agent、Android V1.5 兼容 APK、源码、文档和设计规范。
- 目录结构检查：Server 与 Windows Agent 包第一层仅保留中文 `.cmd` 入口和说明，程序依赖位于 `app`，辅助脚本位于 `tools`。
- 桌面窗口：Server Manager 与 Windows Agent 已改为比例布局，拖拽窗口时各信息区域一起伸缩。

## 未替代的实机验收

- Windows 亮屏/待机指令只能确认 Windows 已接收电源消息，不能在没有外部传感器的情况下证明物理屏幕亮度变化。
- 黑幕模式已通过窗口和黑色像素采样做软件级确认，仍建议在每台真实显示器上检查全屏覆盖范围。
- Android Agent 当前为原 V1.5 APK 兼容包，本轮未重新编译 Android 源码。
- 真实设备上线后，应逐台验证：连接、心跳、亮屏、待机/黑幕、解除黑幕、断网重连、删除后重注册。

## 自动化报告位置

- 最新烟雾报告：`output/smoke/<runId>/report.json`
- Server 标准输出：`output/smoke/<runId>/server.stdout.log`
- Server 错误输出：`output/smoke/<runId>/server.stderr.log`
