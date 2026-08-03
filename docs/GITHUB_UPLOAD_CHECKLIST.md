# GitHub 上传前检查清单

本清单用于第一次创建 GitHub 仓库以及后续公开发布前复核。

## 必须完成

- [ ] 确认仓库将设为公开还是私有。
- [ ] 选择并添加正式 `LICENSE`。
- [ ] 设置安全问题的私密联系方式，更新 `SECURITY.md`。
- [ ] 运行 `git status --short --ignored`，确认生成物和凭据都被忽略。
- [ ] 确认仓库中没有 `.env`、数据库、`data/`、初始密码文件、证书或签名密钥。
- [ ] 确认 `third_party` 中没有来源不明的旧 APK。
- [ ] 运行 .NET Release 构建和核心回归测试。
- [ ] 运行 Android `assembleDebug`。
- [ ] 检查 GitHub Actions 工作流语法和首次运行结果。
- [ ] 检查 README 中的版本号、安装方式和安全边界。
- [ ] 将现场 IP、设备名、序列号和截图替换为通用演示数据。

## 建议完成

- [ ] 将默认分支命名为 `main`。
- [ ] 启用分支保护，要求 CI 通过后再合并。
- [ ] 启用 Dependabot alerts 和 secret scanning。
- [ ] 启用 Private Vulnerability Reporting。
- [ ] 为第一个稳定版本创建 GitHub Release，并上传正式 ZIP 与 `SHA256SUMS.txt`。
- [ ] 如果仓库公开，删除或泛化与公司现场相关的设备名、截图和局域网地址。

## 首次暂存前检查

```powershell
git status --short --ignored
git ls-files --others --exclude-standard
git diff --no-index -- NUL .gitignore
```

不要使用 `git add -A` 直接跳过检查。首次提交前应逐类确认源码、文档、测试和 GitHub 配置。
