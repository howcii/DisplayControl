# UI 设计预览

`ui-unification-20260729` 保存四端统一 UI 方向的历史预览和最终选型参考，不参与正式程序运行。

重新生成预览图需要 Node.js、Playwright 和可用的 Chromium/Chrome：

```powershell
Set-Location .\design-demos\ui-unification-20260729
npm install --no-save playwright
node .\capture-previews.cjs
```

如果希望使用本机 Chrome，可设置 `CHROME_PATH`。生成的三张 PNG 用于设计评审，不代表实时程序截图。
