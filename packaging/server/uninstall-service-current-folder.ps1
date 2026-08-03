$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$serviceName = 'DisplayControlCenterV11'
$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    sc.exe delete $serviceName | Out-Null
}
Get-NetFirewallRule -DisplayName 'Display Control Center V11 TCP 8080' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
[System.Windows.Forms.MessageBox]::Show('Display Control Server service and firewall rule were removed. The package folder and its data directory were kept.', 'Display Control Center') | Out-Null
