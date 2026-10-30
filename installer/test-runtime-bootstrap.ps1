# SailTool 运行库引导测试脚本
# 用途: 卸载指定前置运行时 → 跑安装包 → 验证是否自动下载安装并跑起来
# 用法:
#   pwsh -File installer\test-runtime-bootstrap.ps1 -Mode dotnet
#   pwsh -File installer\test-runtime-bootstrap.ps1 -Mode winappruntime
#   pwsh -File installer\test-runtime-bootstrap.ps1 -Mode all
#   pwsh -File installer\test-runtime-bootstrap.ps1 -Mode none   # 只跑安装验证，不动系统
#
# 所有路径都从本脚本位置推导，可用参数覆盖；先跑 installer\build-setup.ps1 产出安装包。
param(
    [ValidateSet('dotnet', 'winappruntime', 'all', 'none')]
    [string]$Mode = 'dotnet',

    # 留空则自动用 dist\ 下最新的 SailTool-Setup-*.exe
    [string]$Setup = '',

    [string]$TargetDir = (Join-Path $env:TEMP 'SailToolBootstrapTest'),

    [string]$LogFile = (Join-Path $env:TEMP 'sailtool-bootstrap.log')
)

$ErrorActionPreference = 'Stop'
function Log($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

# installer/ -> 仓库根
$root = Split-Path -Parent $PSScriptRoot

if (-not $Setup) {
    $distDir = Join-Path $root 'dist'
    $Setup = Get-ChildItem $distDir -Filter 'SailTool-Setup-*-x64.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Setup) { throw "在 $distDir 找不到 SailTool-Setup-*-x64.exe，请先运行 installer\build-setup.ps1" }
}
if (-not (Test-Path $Setup)) { throw "安装包不存在: $Setup" }
Log "使用安装包: $Setup"

function Remove-DotNet10 {
    $arp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    $bundle = Get-ItemProperty $arp -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match '^Microsoft Windows Desktop Runtime 10' -and $_.QuietUninstallString } |
        Select-Object -First 1
    if ($bundle) {
        $exe = ($bundle.QuietUninstallString -split '"')[1]
        if (Test-Path $exe) {
            Log "卸载 .NET 10: $($bundle.DisplayName)"
            Start-Process $exe -ArgumentList '/uninstall', '/quiet' -Wait | Out-Null
        }
    }
    # 兜底: 干掉残留 10.0.x 的 MSI 条目
    Get-ItemProperty $arp -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match '(Desktop )?Runtime - 10\.0\.' -and $_.UninstallString } |
        ForEach-Object {
            $guid = [regex]::Match($_.UninstallString, '\{[0-9A-Fa-f\-]+\}').Value
            if ($guid) {
                Log "移除残留 MSI: $($_.DisplayName)"
                Start-Process msiexec.exe -ArgumentList '/x', $guid, '/qn', '/norestart' -Wait | Out-Null
            }
        }
    Start-Sleep 2
    # 同时看两处：系统级安装在 Program Files，用户级 SDK/运行时在 %USERPROFILE%\.dotnet
    $left = (Test-Path 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\10.*') -or
            (Test-Path (Join-Path $env:USERPROFILE '.dotnet\shared\Microsoft.WindowsDesktop.App\10.*'))
    Log ".NET 10 桌面运行时残留: $left"
}

function Remove-WinAppRuntime2 {
    $pkgs = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2*' -ErrorAction SilentlyContinue
    foreach ($p in $pkgs) {
        Log "移除 Appx: $($p.PackageFullName)"
        Remove-AppxPackage -Package $p.PackageFullName -ErrorAction SilentlyContinue
    }
    Start-Sleep 2
    $left = (Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2*' -ErrorAction SilentlyContinue | Measure-Object).Count
    Log "WindowsAppRuntime.2 剩余包数: $left"
}

if ($Mode -in @('dotnet', 'all')) { Remove-DotNet10 }
if ($Mode -in @('winappruntime', 'all')) { Remove-WinAppRuntime2 }

Log "开始安装: $Setup"
Remove-Item $TargetDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $LogFile -Force -ErrorAction SilentlyContinue
$p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
    '/NOICONS', "/DIR=$TargetDir", "/LOG=$LogFile" -Wait -PassThru
Log "安装退出码: $($p.ExitCode)"
Select-String -Path $LogFile -Pattern 'Downloading temporary|prereq |PrepareToInstall failed' |
    ForEach-Object { Log ("  " + $_.Line) }

$exe = Join-Path $TargetDir 'SailTool.exe'
if (Test-Path $exe) {
    $app = Start-Process $exe -PassThru
    Start-Sleep 10
    $alive = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
    Log "应用启动: alive=$([bool]$alive) title=$($alive.MainWindowTitle) crash=$(Test-Path (Join-Path $TargetDir 'crash.log'))"
    if ($alive) { Stop-Process -Id $alive.Id -Force }
    $unins = Join-Path $TargetDir 'unins000.exe'
    if (Test-Path $unins) {
        Start-Process $unins -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait | Out-Null
        Log "卸载完成，残留文件: $((Get-ChildItem $TargetDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count)"
    }
} else {
    Log "安装目录不存在，安装失败"
}
