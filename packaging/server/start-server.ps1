$ErrorActionPreference = 'Stop'
$toolsRoot = Split-Path -Parent $PSCommandPath
$root = Split-Path -Parent $toolsRoot
$appRoot = Join-Path $root 'app'
Set-Location $appRoot
New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
$dataPath = Join-Path $root 'data'
$dotnet = Join-Path $appRoot 'dotnet\dotnet.exe'
if (Test-Path -LiteralPath $dotnet) {
    & $dotnet (Join-Path $appRoot 'DisplayControl.Server.dll') "--Storage:DataPath=$dataPath"
} else {
    & (Join-Path $appRoot 'DisplayControl.Server.exe') "--Storage:DataPath=$dataPath"
}
