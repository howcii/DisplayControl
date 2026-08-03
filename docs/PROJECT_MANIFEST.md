# 完整项目清单

## 项目目标

展示屏控制系统用于多台展示屏和会议一体机的集中管理。管理员通过网页查看设备
连接与真实屏幕状态，并向 Windows 或 Android 展示端发送亮屏、休眠和黑幕命令。

## 四个正式程序

| 编号 | 程序 | 交付方式 | 源码 |
| --- | --- | --- | --- |
| 1 | 控制网页 | 随 Server 包发布，由 Server 提供 | `src/DisplayControl.Server/wwwroot` |
| 2 | Windows Server 管理端 | Windows x64 文件夹/ZIP | `src/DisplayControl.Server`、`src/DisplayControl.Server.Manager` |
| 3 | Windows Agent | Windows x64 文件夹/ZIP | `src/DisplayControl.Agent` |
| 4 | Android Agent | APK/ZIP | `src/DisplayControl.AndroidAgent` |

`DisplayControl.Contracts` 是 Server 与 Windows Agent 共用的协议程序集，不是第五个
用户程序。

## 完整交付包

```text
DisplayControlProject-*-full/
├─ 打开服务器管理器.cmd
├─ 启动Windows Agent.cmd
├─ 选择安装位置.cmd
├─ 先看这里-安装说明.txt
├─ 完整项目文件清单.txt
├─ 01-安装包/
│  ├─ DisplayControlServer-win-x64-*/
│  ├─ DisplayControlAgent-win-x64-*/
│  └─ DisplayControlAndroidAgent-v1.6.9-*/
├─ 02-完整源码/
├─ 03-项目文档/
├─ 04-测试与质量/
├─ 05-设计规范/         # 统一规范、方向确认和预览材料
└─ tools/
```

根目录只保留用户需要双击的入口和说明，其余程序、配置、源码和资料进入二级目录。

## 正式源码与辅助工具边界

- `src` 只放四端正式程序和共享协议。
- `tools/diagnostics` 只放独立诊断工具。
- Android RS232 检测版用于判断设备是否开放串口，不属于正式控制链路，也不进入正式
  安装包。
- `.workspace`、`bin`、`obj`、`build`、`.gradle` 是可重新生成的缓存或结果，
  不属于源码包。

## 发布要求

每次发布必须同时具备：

1. Server、Windows Agent 和 Android Agent 安装包。
2. 控制网页静态文件。
3. 完整源码和中文说明。
4. 当前版本说明与测试报告。
5. ZIP 的 SHA256 校验清单。
6. 0 错误、0 警告的 Release 构建结果。
7. 核心回归、发布烟测和 Android APK 签名验证结果。
