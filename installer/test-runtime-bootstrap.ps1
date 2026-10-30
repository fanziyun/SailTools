# 363FastTool 运行库引导测试脚本
# 用途: 卸载指定前置运行时 → 跑安装包 → 验证是否自动下载安装并跑起来
# 用法:
#   pwsh -File artifacts\installer\test-runtime-bootstrap.ps1 -Mode dotnet
#   pwsh -File artifacts\installer\test-runtime-bootstrap.ps1 -Mode winappruntime
#   pwsh -File artifacts\installer\test-runtime-bootstrap.ps1 -Mode all
#   pwsh -File artifacts\installer\test-runtime-bootstrap.ps1 -Mode none   # 只跑安装验证，不动系统
param(
    [ValidateSet('dotnet', 'winappruntime', 'all', 'none')]
    [string]$Mode = 'dotnet',
    [string]$Setup = 'G:\app\363FastTool\363FastTool\dist\363FastTool-Setup-1.0.0-x64.exe',
    [string]$TargetDir = 'C:\Temp\FTBootstrapTest'
)

$ErrorActionPreference = 'Stop'
function Log($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }

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
    $left = Test-Path 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'
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
$log = 'C:\Temp\ftbootstrap.log'
Remove-Item $log -Force -ErrorAction SilentlyContinue
$p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
    '/NOICONS', "/DIR=$TargetDir", "/LOG=$log" -Wait -PassThru
Log "安装退出码: $($p.ExitCode)"
Select-String -Path $log -Pattern 'Downloading temporary|prereq |PrepareToInstall failed' |
    ForEach-Object { Log ("  " + $_.Line) }

$exe = Join-Path $TargetDir '363FastTool.exe'
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
