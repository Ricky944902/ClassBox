<#
.SYNOPSIS
    Class Box 一键构建脚本：编译 → 发布自包含单文件 → 打包安装程序。

.DESCRIPTION
    默认发布 win-x64 自包含单文件版本，目标机器无需安装 .NET 运行时。
    插件以独立 DLL 形式放在程序目录的 Plugins 子目录，可随时增删。

.PARAMETER Runtime
    目标运行时，默认 win-x64。可选 win-x86 / win-arm64。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER FrameworkDependent
    使用框架依赖模式（体积小，但目标机器必须预装 .NET 8 桌面运行时）。

.PARAMETER SkipInstaller
    只发布程序，不调用 Inno Setup 打包。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -SkipInstaller
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$FrameworkDependent,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$project = Join-Path $root "src\ClassBox\ClassBox.csproj"
$distRoot = Join-Path $root "dist"
$publishDir = Join-Path $distRoot "app"

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Assert-Command([string]$name, [string]$hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "未找到命令 $name。$hint"
    }
}

Write-Step "检查构建环境"
Assert-Command "dotnet" "请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0"
$sdkVersion = (& dotnet --version).Trim()
Write-Host "dotnet SDK: $sdkVersion"

$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match "^8\." })) {
    throw "未检测到 .NET 8 SDK，当前仅安装：$($sdks -join '; ')"
}

Write-Step "清理输出目录"
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Step "发布程序（$Runtime / $Configuration）"
$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
$singleFile = if ($FrameworkDependent) { "false" } else { "true" }

$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $publishDir,
    "-p:SelfContained=$selfContained",
    "-p:PublishSingleFile=$singleFile",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:PublishReadyToRun=false",
    "-p:DebugType=none",
    "-p:GenerateDocumentationFile=false",
    "--nologo"
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

Write-Step "校验发布产物"
$exePath = Join-Path $publishDir "ClassBox.exe"
if (-not (Test-Path $exePath)) { throw "发布目录缺少 ClassBox.exe" }

$pluginsDir = Join-Path $publishDir "Plugins"
if (-not (Test-Path $pluginsDir)) { throw "发布目录缺少 Plugins 子目录" }

$pluginCount = (Get-ChildItem $pluginsDir -Filter "*.dll" -File).Count
if ($pluginCount -eq 0) { throw "Plugins 目录中没有插件 DLL" }

$exeSize = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "ClassBox.exe 大小: $exeSize MB"
Write-Host "插件数量: $pluginCount"

if (-not $FrameworkDependent) {
    $coreclr = Get-ChildItem $publishDir -Filter "coreclr.dll" -File -ErrorAction SilentlyContinue
    if ($coreclr) { throw "自包含单文件发布异常：目录中仍存在 coreclr.dll" }
    Write-Host "自包含单文件校验通过：运行时已内嵌，目标机器无需安装 .NET。" -ForegroundColor Green
}

$fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
Write-Host "发布文件总数: $fileCount（单文件 exe + Plugins 目录）"

if ($SkipInstaller) {
    Write-Step "已跳过安装包构建"
    Write-Host "程序目录：$publishDir"
    return
}

Write-Step "查找 Inno Setup"
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "未找到 Inno Setup，已跳过安装包构建。安装后重跑本脚本即可：https://jrsoftware.org/isdl.php"
    Write-Host "程序目录：$publishDir"
    return
}

Write-Host "ISCC: $iscc"

Write-Step "构建安装包"
Push-Location (Join-Path $root "installer")
try {
    & $iscc "/DMyAppVersion=1.4.1" "/DMySourceDir=$publishDir" "/O$distRoot" "ClassBox.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败，退出码 $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Step "构建完成"
Get-ChildItem $distRoot -Filter "*.exe" -File | ForEach-Object {
    Write-Host ("安装包: {0} ({1} MB)" -f $_.Name, [math]::Round($_.Length / 1MB, 1)) -ForegroundColor Green
}
Write-Host "程序目录: $publishDir"
