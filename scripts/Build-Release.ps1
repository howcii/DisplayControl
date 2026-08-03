[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$DotnetPath,
    [string]$NuGetPackagesPath,
    [string]$NodePath,
    [string]$AndroidGradlePath,
    [string]$AndroidSdkRoot,
    [string]$AndroidJavaHome,
    [string]$AndroidGradleUserHome,
    [string]$AndroidUserHome,
    [string]$WorkspaceRoot,
    [string]$AndroidBuildRoot,
    [string]$AndroidBuildSource
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$androidSource = Join-Path $repoRoot 'src\DisplayControl.AndroidAgent'
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = if ([string]::IsNullOrWhiteSpace($env:DISPLAYCONTROL_WORKSPACE)) {
        Join-Path $repoRoot '.workspace'
    }
    else {
        $env:DISPLAYCONTROL_WORKSPACE
    }
}
$workspaceFullPath = [IO.Path]::GetFullPath($WorkspaceRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $workspaceFullPath 'artifacts\release'
}
$outputFullPath = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$workspacePrefix = $workspaceFullPath.TrimEnd('\') + '\'
if (-not $outputFullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -and
    -not $outputFullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot 必须位于项目目录或 WorkspaceRoot 内：$repoRoot / $workspaceFullPath"
}

if ([string]::IsNullOrWhiteSpace($DotnetPath) -or -not (Test-Path -LiteralPath $DotnetPath)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $DotnetPath = $dotnetCommand.Source
}

if (-not [string]::IsNullOrWhiteSpace($NuGetPackagesPath)) {
    $env:NUGET_PACKAGES = $NuGetPackagesPath
}

if ([string]::IsNullOrWhiteSpace($AndroidGradlePath)) {
    $wrapperPath = Join-Path $androidSource 'gradlew.bat'
    if (Test-Path -LiteralPath $wrapperPath) {
        $AndroidGradlePath = $wrapperPath
    }
    else {
        $gradleCommand = Get-Command gradle.bat, gradle -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($gradleCommand) {
            $AndroidGradlePath = $gradleCommand.Source
        }
    }
}

if ([string]::IsNullOrWhiteSpace($AndroidSdkRoot)) {
    $AndroidSdkRoot = if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) {
        $env:ANDROID_SDK_ROOT
    }
    else {
        $env:ANDROID_HOME
    }
}

if ([string]::IsNullOrWhiteSpace($AndroidJavaHome)) {
    $AndroidJavaHome = $env:JAVA_HOME
}

if ([string]::IsNullOrWhiteSpace($AndroidGradleUserHome)) {
    $AndroidGradleUserHome = if ([string]::IsNullOrWhiteSpace($env:GRADLE_USER_HOME)) {
        Join-Path $workspaceFullPath 'gradle-home'
    }
    else {
        $env:GRADLE_USER_HOME
    }
}

if ([string]::IsNullOrWhiteSpace($AndroidUserHome)) {
    $AndroidUserHome = Join-Path $workspaceFullPath 'android-user-home'
}

if ([string]::IsNullOrWhiteSpace($AndroidBuildRoot)) {
    $AndroidBuildRoot = Join-Path $workspaceFullPath 'build-cache\android-agent-v16'
}

if ([string]::IsNullOrWhiteSpace($AndroidBuildSource)) {
    $AndroidBuildSource = Join-Path $workspaceFullPath 'android-src-v16-work'
}

$releaseTag = Get-Date -Format 'yyyyMMdd-HHmm'
$serverName = "DisplayControlServer-win-x64-$releaseTag"
$agentName = "DisplayControlAgent-win-x64-$releaseTag"
$androidName = "DisplayControlAndroidAgent-v1.6.9-$releaseTag"
$fullName = "DisplayControlProject-$releaseTag-full"
$sourceName = "DisplayControlSource-$releaseTag"

if (Test-Path -LiteralPath $outputFullPath) {
    # 目标已被上面的绝对路径检查限制在本项目 artifacts 下。
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}

$stageRoot = Join-Path $outputFullPath 'stage'
$packageRoot = Join-Path $outputFullPath 'packages'
$serverPath = Join-Path $stageRoot $serverName
$agentPath = Join-Path $stageRoot $agentName
$androidPath = Join-Path $stageRoot $androidName
$fullPath = Join-Path $stageRoot $fullName
$fullInstallPath = Join-Path $fullPath '01-安装包'
$fullSourcePath = Join-Path $fullPath '02-完整源码'
$fullDocsPath = Join-Path $fullPath '03-项目文档'
$fullQualityPath = Join-Path $fullPath '04-测试与质量'
$fullDesignPath = Join-Path $fullPath '05-设计规范'
$managerTemp = Join-Path $outputFullPath 'manager-publish'
$agentPublishPath = Join-Path $outputFullPath 'agent-publish'
$serverAppPath = Join-Path $serverPath 'app'
$serverToolsPath = Join-Path $serverPath 'tools'
$serverDocsPath = Join-Path $serverPath 'docs'
$agentToolsPath = Join-Path $agentPath 'tools'
$agentDocsPath = Join-Path $agentPath 'docs'
$agentConfigPath = Join-Path $agentPath 'config'
New-Item -ItemType Directory -Path $stageRoot, $packageRoot | Out-Null

function Invoke-Publish {
    param(
        [Parameter(Mandatory)]
        [string]$Project,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    & $DotnetPath publish $Project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        --output $Destination
    if ($LASTEXITCODE -ne 0) {
        throw "发布失败：$Project"
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force |
        Copy-Item -Destination $Destination -Recurse -Force
}

function Compress-Folder {
    param(
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Force
    }
    Compress-Archive -Path $Source -DestinationPath $Destination -CompressionLevel Optimal
}

function Copy-PackageEntrypoints {
    param(
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$ToolsDestination,
        [Parameter(Mandatory)]
        [string]$DocsDestination
    )

    New-Item -ItemType Directory -Path $ToolsDestination, $DocsDestination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -File |
        Where-Object { $_.Extension -eq '.cmd' } |
        Copy-Item -Destination $ToolsDestination -Force
    Get-ChildItem -LiteralPath $Source -File -Filter '*.ps1' |
        Copy-Item -Destination $ToolsDestination -Force
    Get-ChildItem -LiteralPath $Source -File -Filter '*.txt' |
        Copy-Item -Destination $DocsDestination -Force
}

function Copy-AndroidAdbTools {
    param(
        [Parameter(Mandatory)]
        [string]$Destination
    )

    $platformTools = Join-Path $AndroidSdkRoot 'platform-tools'
    $adbFiles = @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll')
    if (-not (Test-Path -LiteralPath $platformTools)) {
        Write-Warning "未找到 Android platform-tools，ADB 授权工具将不会内置：$platformTools"
        return
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in $adbFiles) {
        $sourceFile = Join-Path $platformTools $file
        if (Test-Path -LiteralPath $sourceFile) {
            Copy-Item -LiteralPath $sourceFile -Destination $Destination -Force
        }
    }
}

function Assert-UnderPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Root
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "路径不在允许目录内：$fullPath"
    }
    return $fullPath
}

function Invoke-AndroidAgentBuild {
    param(
        [Parameter(Mandatory)]
        [string]$Destination
    )

    if ([string]::IsNullOrWhiteSpace($AndroidGradlePath) -or
        -not (Test-Path -LiteralPath $AndroidGradlePath)) {
        throw "缺少 Android Gradle：$AndroidGradlePath"
    }
    if ([string]::IsNullOrWhiteSpace($AndroidJavaHome) -or
        -not (Test-Path -LiteralPath (Join-Path $AndroidJavaHome 'bin\java.exe'))) {
        throw "缺少 JDK：$AndroidJavaHome"
    }
    if ([string]::IsNullOrWhiteSpace($AndroidSdkRoot) -or
        -not (Test-Path -LiteralPath (Join-Path $AndroidSdkRoot 'platforms\android-35'))) {
        throw "缺少 Android SDK platform android-35：$AndroidSdkRoot"
    }

    if (-not (Test-Path -LiteralPath $androidSource)) {
        throw "缺少 Android Agent V1.6 源码：$androidSource"
    }

    $safeBuildSource = Assert-UnderPath -Path $AndroidBuildSource -Root $workspaceFullPath
    $safeBuildRoot = Assert-UnderPath -Path $AndroidBuildRoot -Root $workspaceFullPath
    if (Test-Path -LiteralPath $safeBuildSource) {
        try {
            Remove-Item -LiteralPath $safeBuildSource -Recurse -Force
        }
        catch {
            $previousJavaHomeForStop = $env:JAVA_HOME
            $previousGradleUserHomeForStop = $env:GRADLE_USER_HOME
            try {
                $env:JAVA_HOME = $AndroidJavaHome
                $env:GRADLE_USER_HOME = $AndroidGradleUserHome
                & $AndroidGradlePath --stop | Out-Host
            }
            finally {
                $env:JAVA_HOME = $previousJavaHomeForStop
                $env:GRADLE_USER_HOME = $previousGradleUserHomeForStop
            }
            Start-Sleep -Seconds 1
            Remove-Item -LiteralPath $safeBuildSource -Recurse -Force
        }
    }
    New-Item -ItemType Directory -Path $safeBuildSource -Force | Out-Null
    robocopy $androidSource $safeBuildSource /E /XD build .gradle /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "同步 Android 源码失败，robocopy=$LASTEXITCODE"
    }

    $previousJavaHome = $env:JAVA_HOME
    $previousAndroidSdkRoot = $env:ANDROID_SDK_ROOT
    $previousAndroidHome = $env:ANDROID_HOME
    $previousGradleUserHome = $env:GRADLE_USER_HOME
    $previousAndroidUserHome = $env:ANDROID_USER_HOME
    try {
        $env:JAVA_HOME = $AndroidJavaHome
        $env:ANDROID_SDK_ROOT = $AndroidSdkRoot
        $env:ANDROID_HOME = $AndroidSdkRoot
        $env:GRADLE_USER_HOME = $AndroidGradleUserHome
        $env:ANDROID_USER_HOME = $AndroidUserHome
        $androidBuildRootArg = "-PdisplayControlAndroidBuildRoot=$safeBuildRoot"
        & $AndroidGradlePath -p $safeBuildSource $androidBuildRootArg :app:assembleRelease
        if ($LASTEXITCODE -ne 0) {
            throw 'Android Agent V1.6 编译失败。'
        }
    }
    finally {
        $env:JAVA_HOME = $previousJavaHome
        $env:ANDROID_SDK_ROOT = $previousAndroidSdkRoot
        $env:ANDROID_HOME = $previousAndroidHome
        $env:GRADLE_USER_HOME = $previousGradleUserHome
        $env:ANDROID_USER_HOME = $previousAndroidUserHome
    }

    $apk = Join-Path $safeBuildRoot 'app\outputs\apk\release\app-release.apk'
    if (-not (Test-Path -LiteralPath $apk)) {
        throw "Android Agent V1.6 APK 未生成：$apk"
    }

    New-Item -ItemType Directory -Path $Destination | Out-Null
    Copy-Item -LiteralPath $apk -Destination (Join-Path $Destination 'DisplayControlAndroidAgent-v1.6.9.apk') -Force
    Copy-DirectoryContents -Source (Join-Path $repoRoot 'packaging\android') -Destination $Destination
    Copy-AndroidAdbTools -Destination (Join-Path $Destination 'tools\adb')
    $androidHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Destination 'DisplayControlAndroidAgent-v1.6.9.apk')).Hash
    $androidHash | Set-Content -LiteralPath (Join-Path $Destination 'DisplayControlAndroidAgent-v1.6.9.sha256.txt') -Encoding ascii
}

$solutionPath = Join-Path $repoRoot 'DisplayControl.slnx'
& $DotnetPath restore $solutionPath `
    --runtime win-x64 `
    --ignore-failed-sources `
    -p:TreatWarningsAsErrors=false `
    -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) {
    throw 'Release 依赖恢复失败，已停止打包。'
}

& $DotnetPath build $solutionPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw 'Release 编译失败，已停止打包。'
}

& $DotnetPath run `
    --project (Join-Path $repoRoot 'tests\DisplayControl.Contracts.Tests\DisplayControl.Contracts.Tests.csproj') `
    --configuration Release `
    --no-build
if ($LASTEXITCODE -ne 0) {
    throw '核心回归测试失败，已停止打包。'
}

Invoke-Publish `
    -Project (Join-Path $repoRoot 'src\DisplayControl.Server\DisplayControl.Server.csproj') `
    -Destination $serverAppPath
Invoke-Publish `
    -Project (Join-Path $repoRoot 'src\DisplayControl.Server.Manager\DisplayControl.Server.Manager.csproj') `
    -Destination $managerTemp
Copy-Item `
    -LiteralPath (Join-Path $managerTemp 'DisplayControl.Server.Manager.exe') `
    -Destination (Join-Path $serverPath 'DisplayControl.Server.Manager.exe') `
    -Force
Copy-PackageEntrypoints `
    -Source (Join-Path $repoRoot 'packaging\server') `
    -ToolsDestination $serverToolsPath `
    -DocsDestination $serverDocsPath
New-Item -ItemType Directory -Path (Join-Path $serverPath 'data') -Force | Out-Null

$smokeScript = Join-Path $repoRoot 'scripts\Test-Release.ps1'
if ([string]::IsNullOrWhiteSpace($NodePath)) {
    & $smokeScript -ServerDirectory $serverPath -Port 18085
}
else {
    & $smokeScript -ServerDirectory $serverPath -Port 18085 -NodePath $NodePath
}

Invoke-Publish `
    -Project (Join-Path $repoRoot 'src\DisplayControl.Agent\DisplayControl.Agent.csproj') `
    -Destination $agentPublishPath
New-Item -ItemType Directory -Path $agentConfigPath -Force | Out-Null
Copy-Item `
    -LiteralPath (Join-Path $agentPublishPath 'DisplayControl.Agent.exe') `
    -Destination (Join-Path $agentPath 'DisplayControl.Agent.exe') `
    -Force
Copy-Item `
    -LiteralPath (Join-Path $agentPublishPath 'appsettings.json') `
    -Destination (Join-Path $agentConfigPath 'appsettings.json') `
    -Force
Copy-PackageEntrypoints `
    -Source (Join-Path $repoRoot 'packaging\agent') `
    -ToolsDestination $agentToolsPath `
    -DocsDestination $agentDocsPath

Invoke-AndroidAgentBuild -Destination $androidPath

Copy-DirectoryContents -Source (Join-Path $repoRoot 'packaging\full') -Destination $fullPath
New-Item -ItemType Directory -Path $fullInstallPath, $fullDocsPath, $fullQualityPath, $fullDesignPath -Force | Out-Null
Copy-Item -LiteralPath $serverPath -Destination $fullInstallPath -Recurse
Copy-Item -LiteralPath $agentPath -Destination $fullInstallPath -Recurse
Copy-Item -LiteralPath $androidPath -Destination $fullInstallPath -Recurse
Copy-DirectoryContents -Source (Join-Path $repoRoot 'docs') -Destination $fullDocsPath
Copy-DirectoryContents -Source (Join-Path $repoRoot 'design-system') -Destination $fullDesignPath
Copy-Item -LiteralPath (Join-Path $repoRoot 'direction-approved.md') -Destination $fullDesignPath -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'design-demos') -Destination $fullDesignPath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $fullDocsPath -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'CHANGELOG.md') -Destination $fullDocsPath -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\TEST_REPORT_CURRENT.md') -Destination $fullQualityPath -Force

$sourcePath = Join-Path $stageRoot $sourceName
New-Item -ItemType Directory -Path $sourcePath | Out-Null
foreach ($fileName in @(
    '.gitignore',
    'AGENTS.md',
    'Directory.Build.props',
    'DisplayControl.slnx',
    'direction-approved.md',
    'global.json',
    'README.md',
    'CHANGELOG.md'
)) {
    $sourceFile = Join-Path $repoRoot $fileName
    if (Test-Path -LiteralPath $sourceFile) {
        Copy-Item -LiteralPath $sourceFile -Destination $sourcePath
    }
}
foreach ($directoryName in @(
    'design-demos',
    'design-system',
    'docs',
    'packaging',
    'scripts',
    'src',
    'tests',
    'tools',
    'third_party'
)) {
    $sourceDirectory = Join-Path $repoRoot $directoryName
    $destinationDirectory = Join-Path $sourcePath $directoryName
    robocopy $sourceDirectory $destinationDirectory /E /XD bin obj build .gradle data artifacts output /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "复制源码目录失败：$directoryName，robocopy=$LASTEXITCODE"
    }
}
Copy-DirectoryContents -Source $sourcePath -Destination $fullSourcePath

$projectManifest = @"
展示屏控制系统完整项目文件清单
==============================

对外版本：2026.08.03.1 直接运行版
兼容组合：Server 11.7.0 + Windows Agent 16.6 + Android Agent V1.6.9

正式程序：
1. 控制网页：01-安装包中的 Server 包负责提供。
2. Windows Server 管理端：01-安装包\DisplayControlServer-win-x64-*。
3. Windows Agent：01-安装包\DisplayControlAgent-win-x64-*。
4. Android Agent：01-安装包\DisplayControlAndroidAgent-v1.6.9-*。

项目资料：
- 02-完整源码：可维护源码、构建脚本、测试和辅助诊断源码。
- 03-项目文档：项目说明、部署、结构、版本和维护文档。
- 04-测试与质量：当前发布测试报告。
- 05-设计规范：四端统一 UI 规范。

RS232 串口检测版属于独立诊断工具，不进入正式安装包。
"@
$projectManifest | Set-Content -LiteralPath (Join-Path $fullPath '完整项目文件清单.txt') -Encoding utf8

$requiredProjectItems = @(
    (Join-Path $fullInstallPath $serverName),
    (Join-Path $fullInstallPath $agentName),
    (Join-Path $fullInstallPath $androidName),
    (Join-Path $fullSourcePath 'src\DisplayControl.Server'),
    (Join-Path $fullSourcePath 'src\DisplayControl.Server.Manager'),
    (Join-Path $fullSourcePath 'src\DisplayControl.Agent'),
    (Join-Path $fullSourcePath 'src\DisplayControl.AndroidAgent'),
    (Join-Path $fullSourcePath 'tests'),
    (Join-Path $fullDocsPath 'PROJECT_MANIFEST.md'),
    (Join-Path $fullQualityPath 'TEST_REPORT_CURRENT.md'),
    (Join-Path $fullDesignPath 'MASTER.md'),
    (Join-Path $fullDesignPath 'direction-approved.md')
)
foreach ($requiredItem in $requiredProjectItems) {
    if (-not (Test-Path -LiteralPath $requiredItem)) {
        throw "完整项目缺少必要内容：$requiredItem"
    }
}

$unexpectedSourceDirectories = Get-ChildItem -LiteralPath $fullSourcePath -Directory -Recurse |
    Where-Object { $_.Name -in @('bin', 'obj', 'build', '.gradle', 'artifacts', 'output') }
if ($unexpectedSourceDirectories) {
    $unexpectedNames = ($unexpectedSourceDirectories.FullName -join [Environment]::NewLine)
    throw "完整源码混入了可再生成目录：$unexpectedNames"
}

$unexpectedRootFiles = Get-ChildItem -LiteralPath $fullPath -File |
    Where-Object { $_.Extension -notin @('.cmd', '.txt') }
if ($unexpectedRootFiles) {
    $unexpectedNames = ($unexpectedRootFiles.Name -join ', ')
    throw "完整项目根目录出现非入口文件：$unexpectedNames"
}

@"
完整项目结构检查：通过
检查时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
必要项目：$($requiredProjectItems.Count) 项全部存在
源码缓存：未发现 bin、obj、build、.gradle、artifacts、output
根目录文件：仅包含 .cmd 入口和 .txt 说明
"@ | Set-Content -LiteralPath (Join-Path $fullQualityPath 'PROJECT_LAYOUT_CHECK.txt') -Encoding utf8

Compress-Folder -Source $serverPath -Destination (Join-Path $packageRoot "$serverName.zip")
Compress-Folder -Source $agentPath -Destination (Join-Path $packageRoot "$agentName.zip")
Compress-Folder -Source $androidPath -Destination (Join-Path $packageRoot "$androidName.zip")
Compress-Folder -Source $sourcePath -Destination (Join-Path $packageRoot "$sourceName.zip")
Compress-Folder -Source $fullPath -Destination (Join-Path $packageRoot "$fullName.zip")

$manifest = Get-ChildItem -LiteralPath $packageRoot -Filter '*.zip' |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        "$hash  $($_.Name)"
    }
$manifest | Set-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt') -Encoding ascii

Remove-Item -LiteralPath $managerTemp, $agentPublishPath -Recurse -Force

Write-Host "发布构建完成：$packageRoot"
Get-ChildItem -LiteralPath $packageRoot |
    Select-Object Name, Length, LastWriteTime |
    Format-Table -AutoSize
