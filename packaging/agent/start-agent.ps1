$ErrorActionPreference = 'Stop'
$toolsRoot = Split-Path -Parent $PSCommandPath
$root = Split-Path -Parent $toolsRoot
$exe = Join-Path $root 'DisplayControl.Agent.exe'
Set-Location $root
& $exe
