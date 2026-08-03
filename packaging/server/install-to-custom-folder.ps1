$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$toolsRoot = Split-Path -Parent $PSCommandPath
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $toolsRoot)).TrimEnd('\')
$packageName = Split-Path -Leaf $sourceRoot

$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
$dialog.Description = "请选择 Server 安装位置。程序会在所选目录下创建 $packageName 文件夹。"
$dialog.ShowNewFolderButton = $true
if (Test-Path -LiteralPath 'A:\') {
    $dialog.SelectedPath = 'A:\'
}

if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
    exit 0
}

$destinationRoot = [IO.Path]::GetFullPath($dialog.SelectedPath).TrimEnd('\')
$destination = [IO.Path]::GetFullPath((Join-Path $destinationRoot $packageName)).TrimEnd('\')
if ($destination.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '安装位置不能选择当前 Server 包目录或其子目录。'
}

if (Test-Path -LiteralPath $destination) {
    $existingItem = Get-ChildItem -LiteralPath $destination -Force -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existingItem) {
        throw "目标文件夹已存在且不为空：$destination`r`n请选择一个空目录，或换一个安装位置。"
    }
} else {
    New-Item -ItemType Directory -Path $destination | Out-Null
}

Get-ChildItem -LiteralPath $sourceRoot -Force |
    Where-Object { $_.FullName -ne $destination } |
    Copy-Item -Destination $destination -Recurse -Force

[System.Windows.Forms.MessageBox]::Show(
    "Server 已复制到：`r`n$destination`r`n`r`n请从新位置双击【DisplayControl.Server.Manager.exe】。",
    'Display Control Server',
    [System.Windows.Forms.MessageBoxButtons]::OK,
    [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
Start-Process explorer.exe -ArgumentList "`"$destination`""
