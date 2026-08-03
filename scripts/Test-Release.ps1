[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ServerDirectory,
    [int]$Port = 18085,
    [string]$NodePath,
    [string]$ResultsRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = if ([string]::IsNullOrWhiteSpace($env:DISPLAYCONTROL_WORKSPACE)) {
    Join-Path $repoRoot '.workspace'
}
else {
    $env:DISPLAYCONTROL_WORKSPACE
}
$workspaceFullPath = [IO.Path]::GetFullPath($workspaceRoot)
if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
    $ResultsRoot = Join-Path $workspaceFullPath 'output\smoke'
}
$packageRoot = [IO.Path]::GetFullPath($ServerDirectory)
$appCandidate = Join-Path $packageRoot 'app'
$serverRoot = if (
    (Test-Path -LiteralPath (Join-Path $appCandidate 'DisplayControl.Server.exe')) -or
    (Test-Path -LiteralPath (Join-Path $appCandidate 'DisplayControl.Server.dll'))) {
    [IO.Path]::GetFullPath($appCandidate)
} else {
    $packageRoot
}
$serverExe = Join-Path $serverRoot 'DisplayControl.Server.exe'
$serverDll = Join-Path $serverRoot 'DisplayControl.Server.dll'
if (-not (Test-Path -LiteralPath $serverExe) -and -not (Test-Path -LiteralPath $serverDll)) {
    throw "找不到服务器程序：$serverRoot"
}

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$resultsFullPath = [IO.Path]::GetFullPath($ResultsRoot)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$workspacePrefix = $workspaceFullPath.TrimEnd('\') + '\'
if (-not $resultsFullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -and
    -not $resultsFullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ResultsRoot 必须位于项目目录或 WorkspaceRoot 内：$repoRoot / $workspaceFullPath"
}
$outputPath = Join-Path $resultsFullPath $runId
$dataPath = Join-Path $outputPath 'data'
$stdoutPath = Join-Path $outputPath 'server.stdout.log'
$stderrPath = Join-Path $outputPath 'server.stderr.log'
$reportPath = Join-Path $outputPath 'report.json'
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null

$baseUrl = "http://127.0.0.1:$Port"
$serverProcess = $null
$checks = [Collections.Generic.List[object]]::new()

function Normalize-ProcessPathEnvironment {
    $processPath = [Environment]::GetEnvironmentVariable('Path', 'Process')
    if ([string]::IsNullOrWhiteSpace($processPath)) {
        $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $processPath = (@($machinePath, $userPath) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ';'
    }

    [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
    [Environment]::SetEnvironmentVariable('Path', $processPath, 'Process')
}

Normalize-ProcessPathEnvironment

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $checks.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        detail = $Detail
        checkedAt = [DateTimeOffset]::Now
    })
    if (-not $Passed) {
        throw "$Name：$Detail"
    }
}

function Quote-Argument {
    param([string]$Value)
    if ($Value -match '[\s"]') {
        return '"' + ($Value -replace '"', '\"') + '"'
    }
    return $Value
}

function Join-Arguments {
    param([string[]]$Values)
    return (($Values | ForEach-Object { Quote-Argument $_ }) -join ' ')
}

function Start-TestServer {
    $arguments = @(
        "--urls=$baseUrl",
        "--Storage:DataPath=$dataPath",
        "--Security:CookieName=DisplayControl.Test.$Port"
    )
    if (Test-Path -LiteralPath $serverExe) {
        $script:serverProcess = Start-Process `
            -FilePath $serverExe `
            -ArgumentList (Join-Arguments $arguments) `
            -WorkingDirectory $serverRoot `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru
    }
    else {
        $dotnetPath = Join-Path $serverRoot 'dotnet\dotnet.exe'
        if (-not (Test-Path -LiteralPath $dotnetPath)) {
            $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
        }
        $script:serverProcess = Start-Process `
            -FilePath $dotnetPath `
            -ArgumentList (Join-Arguments (@($serverDll) + $arguments)) `
            -WorkingDirectory $serverRoot `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru
    }

    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($script:serverProcess.HasExited) {
            throw "服务器提前退出，ExitCode=$($script:serverProcess.ExitCode)"
        }
        try {
            $health = Invoke-WebRequest -Uri "$baseUrl/health" -UseBasicParsing -TimeoutSec 2
            if ($health.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    Add-Check '健康检查' $ready '服务器在 10 秒内返回 HTTP 200'
}

function Stop-TestServer {
    if ($script:serverProcess -and -not $script:serverProcess.HasExited) {
        Stop-Process -Id $script:serverProcess.Id -Force
        $script:serverProcess.WaitForExit(5000) | Out-Null
    }
}

function Get-Captcha {
    param([Microsoft.PowerShell.Commands.WebRequestSession]$Session)
    $captcha = Invoke-RestMethod -Uri "$baseUrl/api/auth/captcha" -WebSession $Session -TimeoutSec 5
    $payload = ($captcha.imageUrl -split ',', 2)[1]
    $svg = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload))
    $codeMatches = [regex]::Matches($svg, '>([A-Za-z2-9])</text>')
    if ($codeMatches.Count -lt 5) {
        throw '无法从测试验证码 SVG 中读取验证码。'
    }
    $code = (-join ($codeMatches | Select-Object -First 5 | ForEach-Object { $_.Groups[1].Value })).ToUpperInvariant()
    return [pscustomobject]@{
        id = $captcha.id
        code = $code.ToLowerInvariant()
    }
}

function Invoke-Login {
    param(
        [Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [string]$Password
    )
    $captcha = Get-Captcha -Session $Session
    $body = @{
        username = 'admin'
        password = $Password
        captchaId = $captcha.id
        captchaCode = $captcha.code
    } | ConvertTo-Json
    return Invoke-RestMethod `
        -Uri "$baseUrl/api/auth/login" `
        -Method Post `
        -ContentType 'application/json' `
        -Body $body `
        -WebSession $Session `
        -TimeoutSec 8
}

function Get-ResponseStatusCode {
    param($ErrorRecord)
    if ($ErrorRecord.Exception.Response) {
        return [int]$ErrorRecord.Exception.Response.StatusCode
    }
    return 0
}

function Resolve-Node {
    $hasConfiguredNode = -not [string]::IsNullOrWhiteSpace($script:NodePath)
    if ($hasConfiguredNode -and (Test-Path -LiteralPath $script:NodePath)) {
        return $script:NodePath
    }
    $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
    if ($nodeCommand) {
        return $nodeCommand.Source
    }
    return $null
}

function Wait-ForCommandAgent {
    param([string]$ReadyFile, [Diagnostics.Process]$Process)
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        if ($Process.HasExited) {
            throw "模拟 Agent 提前退出，ExitCode=$($Process.ExitCode)"
        }
        if (Test-Path -LiteralPath $ReadyFile) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw '模拟 Agent 未在 20 秒内完成注册。'
}

try {
    Start-TestServer

    $anonymousSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $unauthorized = $false
    try {
        Invoke-WebRequest `
            -Uri "$baseUrl/api/screens" `
            -WebSession $anonymousSession `
            -UseBasicParsing `
            -TimeoutSec 5 |
            Out-Null
    }
    catch {
        $unauthorized = (Get-ResponseStatusCode $_) -eq 401
    }
    Add-Check '未登录访问拦截' $unauthorized '未登录访问 /api/screens 返回 401'

    $credentialPath = Join-Path $dataPath 'initial-admin-credentials.txt'
    $credentialReady = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if (Test-Path -LiteralPath $credentialPath) {
            $credentialReady = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    Add-Check '初始凭据生成' $credentialReady '初始密码保存在本次测试 data 目录'
    $passwordLine = Get-Content -LiteralPath $credentialPath |
        Where-Object { $_ -like 'Temporary password:*' } |
        Select-Object -First 1
    $initialPassword = $passwordLine.Substring('Temporary password: '.Length)

    $browserSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Login -Session $browserSession -Password $initialPassword
    Add-Check '验证码登录' ($login.authenticated -eq $true) '正确验证码和初始密码登录成功'
    Add-Check '首次改密标记' ($login.mustChangePassword -eq $true) '首次登录要求修改密码'
    $hasCsrfToken = -not [string]::IsNullOrWhiteSpace($login.csrfToken)
    Add-Check 'CSRF 令牌' $hasCsrfToken '登录返回内存态 CSRF 令牌'

    $session = Invoke-RestMethod -Uri "$baseUrl/api/auth/session" -WebSession $browserSession -TimeoutSec 5
    Add-Check 'Cookie 会话' ($session.authenticated -eq $true) '登录 Cookie 可用于后续会话'

    $csrfRejected = $false
    try {
        Invoke-RestMethod `
            -Uri "$baseUrl/api/screens/batch/TurnOn" `
            -Method Post `
            -ContentType 'application/json' `
            -Body (@{ deviceIds = @(); mode = $null } | ConvertTo-Json) `
            -WebSession $browserSession `
            -TimeoutSec 5 |
            Out-Null
    }
    catch {
        $csrfRejected = (Get-ResponseStatusCode $_) -in 400, 403
    }
    Add-Check 'CSRF 拦截' $csrfRejected '缺少 X-CSRF-Token 的状态变更请求被拒绝'

    $emptyCommandRejected = $false
    try {
        Invoke-RestMethod `
            -Uri "$baseUrl/api/screens/on" `
            -Method Post `
            -Headers @{ 'X-CSRF-Token' = $login.csrfToken } `
            -WebSession $browserSession `
            -TimeoutSec 5 |
            Out-Null
    }
    catch {
        $emptyCommandRejected = (Get-ResponseStatusCode $_) -eq 409
    }
    Add-Check '无在线设备命令拒绝' $emptyCommandRejected '没有在线 Agent 时，控制命令返回 409'

    $newPassword = 'DisplayControl-Smoke-2026!'
    Invoke-RestMethod `
        -Uri "$baseUrl/api/auth/change-password" `
        -Method Post `
        -ContentType 'application/json' `
        -Headers @{ 'X-CSRF-Token' = $login.csrfToken } `
        -Body (@{
            currentPassword = $initialPassword
            newPassword = $newPassword
        } | ConvertTo-Json) `
        -WebSession $browserSession `
        -TimeoutSec 8 |
        Out-Null
    $afterChange = Invoke-RestMethod -Uri "$baseUrl/api/auth/session" -WebSession $browserSession -TimeoutSec 5
    Add-Check '修改密码后退出' ($afterChange.authenticated -eq $false) '改密完成后旧会话失效'

    $browserSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $login = Invoke-Login -Session $browserSession -Password $newPassword
    Add-Check '新密码登录' ($login.authenticated -eq $true) '正式密码可重新登录'

    $firstLoggedInSession = $browserSession
    $secondBrowserSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $secondLogin = Invoke-Login -Session $secondBrowserSession -Password $newPassword
    $oldSessionAfterSecondLogin = Invoke-RestMethod -Uri "$baseUrl/api/auth/session" -WebSession $firstLoggedInSession -TimeoutSec 5
    Add-Check '单管理端登录限制' (
        $secondLogin.authenticated -eq $true -and
        $oldSessionAfterSecondLogin.authenticated -eq $false) '新网页登录会替换旧网页登录'
    $browserSession = $secondBrowserSession
    $login = $secondLogin

    $version = Invoke-RestMethod -Uri "$baseUrl/api/version" -WebSession $browserSession -TimeoutSec 5
    Add-Check '版本接口' ($version.serverVersion -like '11.*') "服务器版本：$($version.serverVersion)"
    $publicVersion = Invoke-RestMethod -Uri "$baseUrl/version" -TimeoutSec 5
    Add-Check '公开版本接口' ($publicVersion.serverVersion -like '11.*') "公开版本：$($publicVersion.serverVersion)"
    $screensResponse = Invoke-WebRequest `
        -Uri "$baseUrl/api/screens" `
        -WebSession $browserSession `
        -UseBasicParsing `
        -TimeoutSec 5
    Add-Check '设备接口' ($screensResponse.StatusCode -eq 200) '已登录可读取空或非空设备列表'

    $androidPollBody = @{
        deviceId = 'android-smoke-001'
        deviceName = 'Android Smoke Agent'
        agentVersion = 'Android Agent V1.5-smoke'
        screenStatus = 'on'
        screenStatusVerified = $true
        screenStatusDetails = 'android smoke poll ready'
    } | ConvertTo-Json -Depth 5
    $androidPoll = Invoke-RestMethod `
        -Uri "$baseUrl/api/android-agent/poll" `
        -Method Post `
        -ContentType 'application/json' `
        -Body $androidPollBody `
        -TimeoutSec 5
    Add-Check 'Android 兼容轮询注册' (
        $androidPoll.connected -eq $true -and
        $androidPoll.hasCommand -eq $false) 'Android V1.5 HTTP poll 可注册并返回暂无命令'

    $screensAfterAndroidPoll = Invoke-RestMethod -Uri "$baseUrl/api/screens" -WebSession $browserSession -TimeoutSec 5
    $androidScreen = @($screensAfterAndroidPoll | Where-Object { $_.id -eq 'android-smoke-001' } | Select-Object -First 1)
    Add-Check 'Android 设备在线入库' (
        $androidScreen.Count -eq 1 -and
        $androidScreen[0].online -eq $true -and
        $androidScreen[0].agentVersion -like 'Android Agent V1.5*') 'Android 轮询后设备列表显示在线'

    $androidUnknownPollBody = @{
        deviceId = 'android-smoke-unknown'
        deviceName = 'Android Unknown State Agent'
        agentVersion = 'Android Agent V1.5-smoke'
        screenStatus = 'unknown'
        screenStatusVerified = $false
        screenStatusDetails = 'Android Agent 尚未产生屏幕状态'
    } | ConvertTo-Json -Depth 5
    Invoke-RestMethod `
        -Uri "$baseUrl/api/android-agent/poll" `
        -Method Post `
        -ContentType 'application/json' `
        -Body $androidUnknownPollBody `
        -TimeoutSec 5 | Out-Null
    $screensAfterAndroidUnknownPoll = Invoke-RestMethod -Uri "$baseUrl/api/screens" -WebSession $browserSession -TimeoutSec 5
    $androidUnknownScreen = @($screensAfterAndroidUnknownPoll | Where-Object { $_.id -eq 'android-smoke-unknown' } | Select-Object -First 1)
    Add-Check 'Android 未知状态兜底' (
        $androidUnknownScreen.Count -eq 1 -and
        $androidUnknownScreen[0].screenStatus -eq 'on' -and
        $androidUnknownScreen[0].screenStatusVerified -eq $false) '旧 Android 首次尚未产生屏幕状态时，网页按亮屏在线显示，不再显示未知'

    $androidCommandStart = Invoke-RestMethod `
        -Uri "$baseUrl/api/screens/android-smoke-001/off?mode=blackout" `
        -Method Post `
        -Headers @{ 'X-CSRF-Token' = $login.csrfToken } `
        -WebSession $browserSession `
        -TimeoutSec 5
    Add-Check 'Android 网页命令下发' (
        -not [string]::IsNullOrWhiteSpace($androidCommandStart.requestId)) '网页控制 API 可向 Android HTTP Agent 下发命令'

    $androidPollWithCommand = Invoke-RestMethod `
        -Uri "$baseUrl/api/android-agent/poll" `
        -Method Post `
        -ContentType 'application/json' `
        -Body $androidPollBody `
        -TimeoutSec 5
    $androidCommandId = [string]$androidPollWithCommand.commandId
    $androidLegacyRequestId = [string]$androidPollWithCommand.command.requestId
    $androidLegacyCommand = [string]$androidPollWithCommand.command.command
    Add-Check 'Android 轮询取得命令' (
        $androidPollWithCommand.hasCommand -eq $true -and
        $androidPollWithCommand.commandName -eq 'screen_off' -and
        $androidLegacyCommand -eq 'screen_off' -and
        $androidPollWithCommand.mode -eq 'blackout' -and
        -not [string]::IsNullOrWhiteSpace($androidCommandId) -and
        $androidLegacyRequestId -eq $androidCommandId) 'Android V1.5 poll 返回旧 APK 需要的 requestId + command + blackout 命令'

    $androidResultBody = @{
        deviceId = 'android-smoke-001'
        deviceName = 'Android Smoke Agent'
        agentVersion = 'Android Agent V1.5-smoke'
        requestId = $androidLegacyRequestId
        command = 'screen_off'
        success = $true
        screenStatus = 'blackout'
        screenStatusVerified = $true
        screenStatusDetails = 'android smoke blackout verified'
    } | ConvertTo-Json -Depth 5
    $androidResult = Invoke-RestMethod `
        -Uri "$baseUrl/api/android-agent/result" `
        -Method Post `
        -ContentType 'application/json' `
        -Body $androidResultBody `
        -TimeoutSec 5
    Add-Check 'Android 结果回执接收' (
        $androidResult.accepted -eq $true -and
        $androidResult.connected -eq $true) 'Android V1.5 result 可回传执行结果'

    $androidSettled = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $executions = Invoke-RestMethod -Uri "$baseUrl/api/command-requests/$($androidCommandStart.requestId)" -WebSession $browserSession -TimeoutSec 5
        $targetExecution = @($executions | Where-Object {
            $_.deviceId -eq 'android-smoke-001' -and
            $_.command -eq 'screen_off_blackout' -and
            $_.stage -eq 'verified' -and
            $_.success -eq $true -and
            $_.observedState -eq 'blackout' -and
            $_.observedStateVerified -eq $true
        } | Select-Object -First 1)
        if ($targetExecution.Count -eq 1) {
            $androidSettled = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    Add-Check 'Android 命令结果收束' $androidSettled 'Android result 回执后，网页命令结果变为 verified blackout'

    Stop-TestServer
    Start-TestServer
    $afterRestart = Invoke-RestMethod -Uri "$baseUrl/api/auth/session" -WebSession $browserSession -TimeoutSec 5
    Add-Check '重启后会话保持' ($afterRestart.authenticated -eq $true) '项目 data 目录中的签名密钥可验证原 Cookie'

    $resolvedNode = Resolve-Node
    $hasNode = -not [string]::IsNullOrWhiteSpace($resolvedNode)
    if ($hasNode -and (Test-Path -LiteralPath $resolvedNode)) {
        $env:DISPLAY_CONTROL_URL = $baseUrl
        $env:AGENT_COUNT = '20'
        $env:HEARTBEATS_PER_AGENT = '30'
        $env:HEARTBEAT_INTERVAL_MS = '50'
        $soakOutput = & $resolvedNode (Join-Path $repoRoot 'tests\load\signalr-soak.mjs')
        $soakExitCode = $LASTEXITCODE
        $soak = $soakOutput | ConvertFrom-Json
        Add-Check `
            'SignalR Agent 并发' `
            ($soakExitCode -eq 0 -and $soak.passed -eq 20 -and $soak.heartbeats -eq 600) `
            "20 台模拟 Agent，心跳 $($soak.heartbeats) 次，失败 $($soak.failed.Count) 台"

        $screensAfterSoak = Invoke-RestMethod -Uri "$baseUrl/api/screens" -WebSession $browserSession -TimeoutSec 5
        $soakDevices = @($screensAfterSoak | Where-Object { $_.id -like 'soak-*' })
        Add-Check 'SignalR 心跳落库' ($soakDevices.Count -eq 20) "服务端记录 $($soakDevices.Count) 台并发模拟 Agent"

        $readyFile = Join-Path $outputPath 'command-agent.ready'
        $commandAgentOut = Join-Path $outputPath 'command-agent.stdout.log'
        $commandAgentErr = Join-Path $outputPath 'command-agent.stderr.log'
        Remove-Item -LiteralPath $readyFile -ErrorAction SilentlyContinue
        $env:COMMAND_AGENT_ID = 'smoke-command-001'
        $env:COMMAND_AGENT_READY_FILE = $readyFile
        $env:COMMAND_AGENT_TIMEOUT_MS = '30000'
        $commandScript = Join-Path $repoRoot 'tests\load\signalr-command-agent.mjs'
        $commandAgent = Start-Process `
            -FilePath $resolvedNode `
            -ArgumentList (Join-Arguments @($commandScript)) `
            -WorkingDirectory $repoRoot `
            -RedirectStandardOutput $commandAgentOut `
            -RedirectStandardError $commandAgentErr `
            -WindowStyle Hidden `
            -PassThru
        Wait-ForCommandAgent -ReadyFile $readyFile -Process $commandAgent
        $commandStart = Invoke-RestMethod `
            -Uri "$baseUrl/api/screens/smoke-command-001/on" `
            -Method Post `
            -Headers @{ 'X-CSRF-Token' = $login.csrfToken } `
            -WebSession $browserSession `
            -TimeoutSec 5
        $hasCommandRequestId = -not [string]::IsNullOrWhiteSpace($commandStart.requestId)
        Add-Check '命令请求编号' $hasCommandRequestId '网页控制 API 返回 requestId'
        $commandAgentExited = $commandAgent.WaitForExit(15000)
        if (-not $commandAgentExited) {
            Stop-Process -Id $commandAgent.Id -Force
            throw '模拟 Agent 未在 15 秒内退出。'
        }
        $commandAgent.Refresh()
        $commandAgentOutput = Get-Content -LiteralPath $commandAgentOut -Raw
        $commandAgentResult = $commandAgentOutput | ConvertFrom-Json
        Add-Check '模拟 Agent 命令回执' ($commandAgentResult.ok -eq $true) '模拟 Agent 已收到并验证控制命令'
        $verified = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $executions = Invoke-RestMethod -Uri "$baseUrl/api/command-requests/$($commandStart.requestId)" -WebSession $browserSession -TimeoutSec 5
            $verified = @($executions | Where-Object { $_.stage -eq 'verified' -and $_.success -eq $true }).Count -eq 1
            if ($verified) {
                break
            }
            Start-Sleep -Milliseconds 250
        }
        Add-Check '命令结果查询' $verified '命令结果接口返回 verified 成功状态'

        $executedOnlyReadyFile = Join-Path $outputPath 'command-agent-executed-only.ready'
        $executedOnlyOut = Join-Path $outputPath 'command-agent-executed-only.stdout.log'
        $executedOnlyErr = Join-Path $outputPath 'command-agent-executed-only.stderr.log'
        Remove-Item -LiteralPath $executedOnlyReadyFile -ErrorAction SilentlyContinue
        $env:COMMAND_AGENT_ID = 'smoke-command-executed-only'
        $env:COMMAND_AGENT_READY_FILE = $executedOnlyReadyFile
        $env:COMMAND_AGENT_TIMEOUT_MS = '30000'
        $env:COMMAND_AGENT_INITIAL_STATE = '0'
        $env:COMMAND_AGENT_INITIAL_VERIFIED = 'false'
        $env:COMMAND_AGENT_FINAL_STAGE = '3'
        $env:COMMAND_AGENT_FINAL_STATE = '2'
        $env:COMMAND_AGENT_FINAL_VERIFIED = 'false'
        $env:COMMAND_AGENT_FINAL_DETAIL = '休眠命令已执行，网页已即时刷新；现场唤醒后 Agent 不再继续压制。'
        $executedOnlyAgent = Start-Process `
            -FilePath $resolvedNode `
            -ArgumentList (Join-Arguments @($commandScript)) `
            -WorkingDirectory $repoRoot `
            -RedirectStandardOutput $executedOnlyOut `
            -RedirectStandardError $executedOnlyErr `
            -WindowStyle Hidden `
            -PassThru
        Wait-ForCommandAgent -ReadyFile $executedOnlyReadyFile -Process $executedOnlyAgent
        $executedOnlyStart = Invoke-RestMethod `
            -Uri "$baseUrl/api/screens/smoke-command-executed-only/off?mode=standby" `
            -Method Post `
            -Headers @{ 'X-CSRF-Token' = $login.csrfToken } `
            -WebSession $browserSession `
            -TimeoutSec 5
        $executedOnlyExited = $executedOnlyAgent.WaitForExit(15000)
        if (-not $executedOnlyExited) {
            Stop-Process -Id $executedOnlyAgent.Id -Force
            throw '仅执行回执模拟 Agent 未在 15 秒内退出。'
        }
        $executedOnlyAgent.Refresh()
        $executedOnlyResult = (Get-Content -LiteralPath $executedOnlyOut -Raw) | ConvertFrom-Json
        Add-Check '仅执行回执模拟 Agent' (
            $executedOnlyResult.ok -eq $true -and
            $executedOnlyResult.finalStage -eq 3) '模拟 Agent 只返回 executed，不返回 verified'

        $executedOnlySettled = $false
        $executedOnlyStateReported = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $executions = Invoke-RestMethod -Uri "$baseUrl/api/command-requests/$($executedOnlyStart.requestId)" -WebSession $browserSession -TimeoutSec 5
            $targetExecution = @($executions | Where-Object {
                $_.deviceId -eq 'smoke-command-executed-only' -and
                $_.command -eq 'screen_off_standby' -and
                $_.stage -eq 'executed' -and
                $_.success -eq $true
            } | Select-Object -First 1)
            if ($targetExecution.Count -eq 1) {
                $executedOnlySettled = $true
                $executedOnlyStateReported = (
                    $targetExecution[0].observedState -eq 'standby' -and
                    $targetExecution[0].observedStateVerified -eq $false)
                break
            }
            Start-Sleep -Milliseconds 250
        }
        Add-Check '电源命令已执行即收束' $executedOnlySettled 'TurnOn/Standby 的 executed 回执不会继续等待到超时'
        Add-Check '电源回执状态不未知' $executedOnlyStateReported '命令结果显示 standby，内部保留未物理验证标记'

        $screensAfterExecutedOnly = Invoke-RestMethod -Uri "$baseUrl/api/screens" -WebSession $browserSession -TimeoutSec 5
        $executedOnlyScreen = @($screensAfterExecutedOnly | Where-Object { $_.id -eq 'smoke-command-executed-only' } | Select-Object -First 1)
        Add-Check '设备列表即时刷新回执状态' (
            $executedOnlyScreen.Count -eq 1 -and
            $executedOnlyScreen[0].screenStatus -eq 'standby' -and
            $executedOnlyScreen[0].screenStatusVerified -eq $false) '设备列表显示 standby，不再显示 unknown'

        $sequenceReadyFile = Join-Path $outputPath 'sequence-agent.ready'
        $sequenceOut = Join-Path $outputPath 'sequence-agent.stdout.log'
        $sequenceErr = Join-Path $outputPath 'sequence-agent.stderr.log'
        Remove-Item -LiteralPath $sequenceReadyFile -ErrorAction SilentlyContinue
        $env:SEQUENCE_AGENT_ID = 'smoke-sequence-001'
        $env:SEQUENCE_AGENT_READY_FILE = $sequenceReadyFile
        $env:SEQUENCE_AGENT_TIMEOUT_MS = '60000'
        $sequenceScript = Join-Path $repoRoot 'tests\load\signalr-sequence-agent.mjs'
        $sequenceAgent = Start-Process `
            -FilePath $resolvedNode `
            -ArgumentList (Join-Arguments @($sequenceScript)) `
            -WorkingDirectory $repoRoot `
            -RedirectStandardOutput $sequenceOut `
            -RedirectStandardError $sequenceErr `
            -WindowStyle Hidden `
            -PassThru
        Wait-ForCommandAgent -ReadyFile $sequenceReadyFile -Process $sequenceAgent
        $sequenceSteps = @(
            @{
                Name = '黑幕1'
                Uri = "$baseUrl/api/screens/smoke-sequence-001/off?mode=blackout"
                Command = 'screen_off_blackout'
                Stage = 'executed'
                State = 'blackout'
                Verified = $false
            },
            @{
                Name = '亮屏1'
                Uri = "$baseUrl/api/screens/smoke-sequence-001/on"
                Command = 'screen_on'
                Stage = 'executed'
                State = 'on'
                Verified = $false
            },
            @{
                Name = '休眠'
                Uri = "$baseUrl/api/screens/smoke-sequence-001/off?mode=standby"
                Command = 'screen_off_standby'
                Stage = 'executed'
                State = 'standby'
                Verified = $false
            },
            @{
                Name = '黑幕2'
                Uri = "$baseUrl/api/screens/smoke-sequence-001/off?mode=blackout"
                Command = 'screen_off_blackout'
                Stage = 'verified'
                State = 'blackout'
                Verified = $true
            },
            @{
                Name = '亮屏2'
                Uri = "$baseUrl/api/screens/smoke-sequence-001/on"
                Command = 'screen_on'
                Stage = 'executed'
                State = 'on'
                Verified = $false
            }
        )

        foreach ($step in $sequenceSteps) {
            $start = Invoke-RestMethod `
                -Uri $step.Uri `
                -Method Post `
                -Headers @{ 'X-CSRF-Token' = $login.csrfToken } `
                -WebSession $browserSession `
                -TimeoutSec 5
            $settled = $false
            for ($attempt = 0; $attempt -lt 30; $attempt++) {
                $executions = Invoke-RestMethod -Uri "$baseUrl/api/command-requests/$($start.requestId)" -WebSession $browserSession -TimeoutSec 5
                $target = @($executions | Where-Object {
                    $_.deviceId -eq 'smoke-sequence-001' -and
                    $_.command -eq $step.Command -and
                    $_.stage -eq $step.Stage -and
                    $_.success -eq $true -and
                    $_.observedState -eq $step.State -and
                    $_.observedStateVerified -eq $step.Verified
                } | Select-Object -First 1)
                if ($target.Count -eq 1) {
                    $settled = $true
                    break
                }
                Start-Sleep -Milliseconds 250
            }
            Add-Check "连续控制 $($step.Name)" $settled "黑幕/亮屏/休眠连续序列中的 $($step.Name) 正常收束"
        }

        $sequenceExited = $sequenceAgent.WaitForExit(15000)
        if (-not $sequenceExited) {
            Stop-Process -Id $sequenceAgent.Id -Force
            throw '连续控制模拟 Agent 未在 15 秒内退出。'
        }
        $sequenceAgent.Refresh()
        $sequenceResult = (Get-Content -LiteralPath $sequenceOut -Raw) | ConvertFrom-Json
        Add-Check '连续控制模拟 Agent' (
            $sequenceResult.ok -eq $true -and
            @($sequenceResult.receivedKinds).Count -eq 5) '模拟 Agent 按顺序收到 黑幕→亮屏→休眠→黑幕→亮屏'
    }
    else {
        $skippedNodeCheck = [pscustomobject]@{
            name = 'SignalR Agent 并发'
            passed = $null
            detail = '未找到 Node.js，本项跳过'
            checkedAt = [DateTimeOffset]::Now
        }
        $checks.Add($skippedNodeCheck)
    }

    $report = [pscustomobject]@{
        product = 'DisplayControl current release'
        runId = $runId
        baseUrl = $baseUrl
        passed = -not ($checks | Where-Object { $_.passed -eq $false })
        checks = $checks
        serverStdout = $stdoutPath
        serverStderr = $stderrPath
    }
    $report | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $reportPath -Encoding utf8
    $report | ConvertTo-Json -Depth 6
}
finally {
    Stop-TestServer
    Remove-Item Env:DISPLAY_CONTROL_URL -ErrorAction SilentlyContinue
    Remove-Item Env:AGENT_COUNT -ErrorAction SilentlyContinue
    Remove-Item Env:HEARTBEATS_PER_AGENT -ErrorAction SilentlyContinue
    Remove-Item Env:HEARTBEAT_INTERVAL_MS -ErrorAction SilentlyContinue
    Remove-Item Env:COMMAND_AGENT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:COMMAND_AGENT_READY_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:COMMAND_AGENT_TIMEOUT_MS -ErrorAction SilentlyContinue
    Remove-Item Env:COMMAND_AGENT_INITIAL_STATE -ErrorAction SilentlyContinue
    Remove-Item Env:COMMAND_AGENT_INITIAL_VERIFIED -ErrorAction SilentlyContinue
    Remove-Item Env:COMMAND_AGENT_FINAL_STAGE -ErrorAction SilentlyContinue
    Remove-Item Env:COMMAND_AGENT_FINAL_STATE -ErrorAction SilentlyContinue
    Remove-Item Env:COMMAND_AGENT_FINAL_VERIFIED -ErrorAction SilentlyContinue
    Remove-Item Env:COMMAND_AGENT_FINAL_DETAIL -ErrorAction SilentlyContinue
    Remove-Item Env:SEQUENCE_AGENT_ID -ErrorAction SilentlyContinue
    Remove-Item Env:SEQUENCE_AGENT_READY_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:SEQUENCE_AGENT_TIMEOUT_MS -ErrorAction SilentlyContinue
}
