$ErrorActionPreference = 'Stop'
$toolsRoot = Split-Path -Parent $PSCommandPath
$root = Split-Path -Parent $toolsRoot
$exe = Join-Path $root 'DisplayControl.Agent.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'DisplayControl.Agent.exe not found. Please run this script from the Windows Agent package folder.'
}

$startup = [Environment]::GetFolderPath('Startup')
$legacyShortcut = Join-Path $startup 'DisplayControlAgentV16.lnk'
$shortcut = Join-Path $startup 'DisplayControlAgent.lnk'
Remove-Item -LiteralPath $legacyShortcut -Force -ErrorAction SilentlyContinue
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.Arguments = '--minimized'
$link.WorkingDirectory = $root
$link.Description = 'Display Control Agent'
$link.Save()
Start-Process -FilePath $exe -ArgumentList '--minimized' -WorkingDirectory $root
