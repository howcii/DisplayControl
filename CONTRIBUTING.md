# 贡献指南

感谢参与 DisplayControl。项目同时包含 Windows、Android、网页和发布脚本，修改前请先确认影响范围。

## 开发约定

- 用户文档、界面文本和交付说明使用中文。
- 代码标识符使用清晰的英文名称。
- 保留 `DisplayControlCenterV11` 服务兼容名和现有协议兼容线。
- 状态必须区分已发送、已接收、已执行和已验证。
- 不提交真实设备地址、管理员凭据、数据库、证书、签名密钥、构建缓存或发布包。

## 本地验证

```powershell
dotnet restore .\DisplayControl.slnx --locked-mode
dotnet build .\DisplayControl.slnx -c Release --no-restore
dotnet run `
  --project .\tests\DisplayControl.Contracts.Tests\DisplayControl.Contracts.Tests.csproj `
  -c Release `
  --no-restore
```

涉及 Android 时还应在只包含 ASCII 字符的路径中运行：

```powershell
Set-Location .\src\DisplayControl.AndroidAgent
.\gradlew.bat :app:assembleDebug
```

涉及发布脚本、登录、设备状态或命令链路时，还应运行 `scripts\Test-Release.ps1` 对生成的
Server 包执行烟雾测试。

## 提交与 Pull Request

- 一次提交只处理一个可独立验证的问题。
- 推荐提交信息格式：`type(scope): 简短说明`。
- PR 说明应列出改动、验证结果、已知限制和回滚方式。
- UI 或交互变化请提供修改前后截图。
- 版本变化应同步 `CHANGELOG.md`、`docs/VERSION_NOTES.md`、组件版本和测试报告。

## 安全问题

不要通过公开 Issue 提交漏洞利用细节、真实密码或公司网络信息。请先阅读
[SECURITY.md](SECURITY.md)。
