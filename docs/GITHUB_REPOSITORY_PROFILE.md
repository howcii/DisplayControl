# GitHub 仓库资料

创建 GitHub 仓库或填写 About 信息时，可直接使用以下内容。

## 仓库名称

`DisplayControl`

## 简短介绍

在可信局域网内，通过网页集中控制 Windows 与 Android 展示屏的亮屏、休眠和黑幕，并以 Agent 回执同步设备状态。

## Topics

```text
display-control
digital-signage
device-management
aspnet-core
signalr
winforms
android
windows
lan
chinese
```

## 首次提交信息

```text
feat: 初始化 DisplayControl 完整项目
```

## 首个 Release

- Tag：`v2026.08.03.1`
- 标题：`DisplayControl 2026.08.03.1 直接运行版`
- 建议附件：Server ZIP、Windows Agent ZIP、Android Agent ZIP、完整项目 ZIP、`SHA256SUMS.txt`
- `DisplayControlSource-*.zip` 不必重复上传，GitHub 会为每个 Tag 自动提供源码压缩包。

### Release 简介

```markdown
首个可维护源码发布版，包含控制网页、Windows Server 管理端、Windows Agent 和 Android Agent。

主要特性：

- Windows Server Manager 与 Windows Agent 为自包含单文件 EXE。
- Android Agent 提供独立 APK。
- 支持亮屏、休眠和全屏黑幕控制。
- 命令区分已发送、已接收、已执行和已验证。
- 管理网页包含验证码、CSRF 防护和单一管理设备登录限制。
- 附带完整中文文档、自动化测试和 SHA256 校验清单。

部署范围：可信局域网。请勿把默认 HTTP 端口直接暴露到互联网。
```

## 公开仓库前仍需确认

- 选择正式 LICENSE。
- 在 `SECURITY.md` 填写安全问题的私密联系方式，或启用 GitHub Private Vulnerability Reporting。
- 确认公司允许将项目源码公开；不能公开时应保持 Private。
