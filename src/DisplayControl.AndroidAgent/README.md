# DisplayControl Android Agent V1.6

这是恢复源码后的 Android 展示端 Agent。当前 APK 内部版本为 V1.6.9。它继续兼容服务器的 `/api/android-agent/poll` 与 `/api/android-agent/result` 轮询协议，同时修复旧 V1.5 APK 的 15 秒轮询、休眠退化为黑幕、回执导致网页按钮卡住等问题。

## 构建要求

- JDK：`A:\CodexTools\jdk-21`
- Android SDK：`A:\CodexTools\android-sdk`
- Gradle：`A:\CodexTools\gradle-8.14.5`

推荐构建命令：

```powershell
$env:JAVA_HOME = 'A:\CodexTools\jdk-21'
$env:ANDROID_SDK_ROOT = 'A:\CodexTools\android-sdk'
$env:ANDROID_HOME = $env:ANDROID_SDK_ROOT
$env:GRADLE_USER_HOME = 'A:\CodexTools\gradle-home'
& 'A:\CodexTools\gradle-8.14.5\bin\gradle.bat' -p 'C:\Users\ASUS\Documents\移动实习 2\DisplayControlWork\src\DisplayControl.AndroidAgent' :app:assembleRelease
```

## 现场逻辑

- 空闲轮询固定约 1 秒，网页命令响应比 V1.5 的 15 秒轮询明显更快。
- 黑幕：打开全屏黑色 Activity，不调用系统锁屏。
- 休眠：依次尝试“设备管理器/系统锁屏权限”、系统睡眠按键、系统设置超时休眠；只有系统回读为非交互状态才标记已验证，不再偷偷退化成黑幕。
- 大屏兼容：系统睡眠按键会连续尝试 `SLEEP`、`SOFT_SLEEP`、`POWER`；系统超时休眠最长等待 20 秒，兼容部分 Android 8 大屏的最短息屏限制。
- 亮屏：解除黑幕并请求系统亮屏；如果设备安全策略禁止应用自动解锁，网页会收到明确回执，现场人员手动唤醒优先。
- 命令去重：同一个 `commandId` 只执行一次，网络抖动时只补发回执，避免用户手动唤醒后又被旧命令压回休眠。
- 前台服务通知：只保留静默后台运行通知，不再在黑幕模式下弹出“服务器连接成功/已连接”提示。
- 面板视图：连接配置放在当前状态之前，大屏横向使用居中内容列，避免字段和状态文本被过度拉宽。
- 面板诊断：提供本机休眠测试、允许系统设置、恢复息屏时间和设备能力状态，方便现场判断定制安卓固件是否拦截普通 APK 休眠。
- ADB 授权：发布包内提供 `启用安卓休眠权限-ADB.cmd` 和最小 ADB 文件，用于普通设备管理界面启用失败时从电脑侧尝试授权。
