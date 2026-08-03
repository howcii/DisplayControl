$ErrorActionPreference = 'Stop'
$startup = [Environment]::GetFolderPath('Startup')
foreach ($shortcutName in @('DisplayControlAgent.lnk', 'DisplayControlAgentV16.lnk')) {
    Remove-Item -LiteralPath (Join-Path $startup $shortcutName) -Force -ErrorAction SilentlyContinue
}

$toolsRoot = Split-Path -Parent $PSCommandPath
$root = Split-Path -Parent $toolsRoot
$appRoot = [IO.Path]::GetFullPath($root)
Get-Process -Name DisplayControl.Agent -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            -not [string]::IsNullOrWhiteSpace($_.Path) -and
                [IO.Path]::GetFullPath($_.Path).StartsWith($appRoot, [StringComparison]::OrdinalIgnoreCase)
        } catch {
            $false
        }
    } |
    Stop-Process -Force
