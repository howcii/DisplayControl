$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$toolsRoot = Split-Path -Parent $PSCommandPath
$root = Split-Path -Parent $toolsRoot
$appRoot = Join-Path $root 'app'
$exe = Join-Path $appRoot 'DisplayControl.Server.exe'
$dll = Join-Path $appRoot 'DisplayControl.Server.dll'
$dotnet = Join-Path $appRoot 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $exe) -and -not (Test-Path -LiteralPath $dll)) {
    throw 'DisplayControl.Server.exe not found. Please run this script from the Server package folder.'
}

$serviceName = 'DisplayControlCenterV11'
$dataPath = Join-Path $root 'data'
$serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
$previousDataPath = $null
$sameRoot = $false

function Select-ExistingDataPath {
    param([string[]] $Candidates)

    $existingCandidates = @()
    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $credentialFile = Join-Path $candidate 'admin-credentials.json'
        if (Test-Path -LiteralPath $credentialFile) {
            $existingCandidates += [pscustomobject]@{
                Path = $candidate
                LastWriteTime = (Get-Item -LiteralPath $credentialFile).LastWriteTimeUtc
            }
        }
    }

    $existingCandidates |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty Path
}

if ($serviceInfo) {
    $serverPathMatch = [regex]::Match(
        $serviceInfo.PathName,
        '(?:"(?<path>[^"]*DisplayControl\.Server\.(?:dll|exe))"|(?<path>\S*DisplayControl\.Server\.(?:dll|exe)))',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($serverPathMatch.Success) {
        $previousBinaryRoot = Split-Path -Parent $serverPathMatch.Groups['path'].Value
        $previousRoot = if ([string]::Equals(
            (Split-Path -Leaf $previousBinaryRoot),
            'app',
            [StringComparison]::OrdinalIgnoreCase)) {
            Split-Path -Parent $previousBinaryRoot
        } else {
            $previousBinaryRoot
        }
        $previousDataPath = Select-ExistingDataPath @(
            (Join-Path $previousRoot 'data'),
            (Join-Path $previousRoot 'app\data'))
        $sameRoot = [string]::Equals(
            [IO.Path]::GetFullPath($previousRoot).TrimEnd('\'),
            [IO.Path]::GetFullPath($root).TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase)
    }
}

$existing = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Stop-Service $serviceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
}

New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
$newDataIsEmpty = -not (Get-ChildItem -LiteralPath $dataPath -Force -ErrorAction SilentlyContinue)
if (-not $sameRoot -and $newDataIsEmpty -and $previousDataPath -and (Test-Path -LiteralPath $previousDataPath)) {
    Get-ChildItem -LiteralPath $previousDataPath -Force |
        Copy-Item -Destination $dataPath -Recurse -Force
}

$binary = if (Test-Path -LiteralPath $dotnet) {
    '"' + $dotnet + '" "' + $dll + '" --service --Storage:DataPath="' + $dataPath + '"'
} else {
    '"' + $exe + '" --service --Storage:DataPath="' + $dataPath + '"'
}

if ($existing) {
    sc.exe config $serviceName binPath= $binary start= auto DisplayName= 'Display Control Center V11' | Out-Null
} else {
    New-Service -Name $serviceName -BinaryPathName $binary -DisplayName 'Display Control Center V11' -Description 'Display control server V11' -StartupType Automatic | Out-Null
}

sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
Start-Service $serviceName
(Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))

try {
    Get-NetFirewallRule -DisplayName 'Display Control Center V11 TCP 8080' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'Display Control Center V11 TCP 8080' -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow -Profile Any -RemoteAddress LocalSubnet | Out-Null
} catch {
    [System.Windows.Forms.MessageBox]::Show('Service is running, but firewall configuration failed: ' + $_.Exception.Message, 'Display Control Center V11') | Out-Null
}

[System.Windows.Forms.MessageBox]::Show("Display Control Server is running from:`r`n$root`r`n`r`nOpen http://127.0.0.1:8080", 'Display Control Center') | Out-Null
