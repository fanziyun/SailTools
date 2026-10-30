# SailTool packaging script (framework-dependent build + runtime bootstrap download)
#
# Usage:  pwsh -File installer/build-setup.ps1
#
# Everything is resolved from this script's own location, so the project packages
# from any checkout directory. Requires Inno Setup 6 at its default per-user path
# and a .NET 10 SDK.
#
# Note on $dotnet: the dotnet on PATH is C:\Program Files\dotnet, which holds only
# runtimes (8.0.33 / 10.0.14) and no SDK — building with it fails with NETSDK1045.
# The usable SDK 10.0.403 lives in the per-user install below. Override with
# -DotNetExe if your SDK is elsewhere.
param(
    [string]$DotNetExe = "$env:USERPROFILE\.dotnet\dotnet.exe",
    [string]$IsccExe   = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'

# installer/ -> repository root
$root      = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $root 'src\SailTool\SailTool.csproj'
$payload   = Join-Path $root 'artifacts\payload-fd2-slim'
$publish   = Join-Path $root 'artifacts\publish-fd2'
$installer = Join-Path $root 'installer'

foreach ($tool in @($DotNetExe, $IsccExe)) {
    if (-not (Test-Path $tool)) { throw "Required tool not found: $tool" }
}

# 1) Single source of truth for the version: read it out of the csproj so the
#    artifact name can never drift from what the app reports about itself.
[xml]$csproj = Get-Content $project
$appVersion = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $appVersion) { throw "No <Version> found in $project" }
Write-Host "Version: $appVersion"

# 2) Framework-dependent publish (.NET and Windows App SDK both external).
#    tools/platform-tools flows into the output on its own via the csproj item
#    group, so there is nothing to copy in afterwards.
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
& $DotNetExe publish $project -c Release -r win-x64 `
    -p:Platform=x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false `
    -p:WindowsPackageType=None -o $publish | Out-Null

# 3) Build the slim payload: drop the AI/ML and debugging weight the installer
#    never ships, keep everything else including platform-tools.
New-Item -ItemType Directory -Force $payload | Out-Null
robocopy $publish $payload /E /NFL /NDL /NJH /NJS /NP | Out-Null
$drop = '^(ONNX|onnx|DirectML|NPUDetect|PerceptiveStreaming|Microsoft\.ML\.OnnxRuntime|Microsoft\.Windows\.AI\.|Microsoft\.Windows\.Internal\.AI\.|Microsoft\.DiaSymReader|mscordaccore|mscordbi|clretwrc|clrgcexp|System\.Numerics\.Tensors)'
Get-ChildItem $payload -File | Where-Object Name -match $drop | Remove-Item -Force
Remove-Item (Join-Path $payload 'SailTool.pdb') -Force -ErrorAction SilentlyContinue

# The publish output must carry the external tools, otherwise the packaged app
# cannot find fastboot.exe and dies in FastbootProcessService's constructor.
$tools = Join-Path $payload 'platform-tools'
foreach ($required in @('adb.exe', 'fastboot.exe')) {
    if (-not (Test-Path (Join-Path $tools $required))) {
        throw "platform-tools\$required missing from publish output — check the csproj tools item group"
    }
}

# 4) Compile the installer; prerequisite runtimes are downloaded at install time.
$outDir  = Join-Path $root 'artifacts\installer\out'
New-Item -ItemType Directory -Force $outDir | Out-Null

& $IsccExe (Join-Path $installer 'SailTool.iss') `
    "/DMyAppVersion=$appVersion" `
    "/DPayloadDir=$payload" `
    "/DMyOutputDir=$outDir" `
    "/DSetupIcon=$(Join-Path $root 'src\SailTool\Assets\AppIcon.ico')" | Select-Object -Last 3

$out     = Join-Path $outDir "SailTool-Setup-$appVersion-x64.exe"
if (-not (Test-Path $out)) { throw "ISCC did not produce $out" }
$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $distDir | Out-Null
$dist = Join-Path $distDir (Split-Path $out -Leaf)
Copy-Item $out $dist -Force

Get-Item $dist | Select-Object FullName, @{n='MB'; e={[math]::Round($_.Length/1MB,1)}}
Get-FileHash $dist -Algorithm SHA256 | Select-Object Hash
