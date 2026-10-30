# 363FastTool 打包脚本（框架依赖版 + 运行时引导下载）
# 用法:  pwsh -File artifacts\installer\build-setup.ps1
$ErrorActionPreference = 'Stop'
$root = 'G:\app\363FastTool\363FastTool'
$dotnet = 'C:\Users\Administrator\.dotnet\dotnet.exe'
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
$payload = Join-Path $root 'artifacts\payload-fd2-slim'

# 1) 框架依赖发布（.NET 与 Windows App SDK 都不自包含）
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
& $dotnet publish (Join-Path $root '363FastTool.csproj') -c Release -r win-x64 `
    -p:Platform=x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false `
    -p:WindowsPackageType=None -o "$root\artifacts\publish-fd2" | Out-Null

# 2) 生成精简 payload（去掉 AI/ML 与调试负载，补入 platform-tools）
New-Item -ItemType Directory -Force $payload | Out-Null
robocopy "$root\artifacts\publish-fd2" $payload /E /NFL /NDL /NJH /NJS /NP | Out-Null
$drop = '^(ONNX|onnx|DirectML|NPUDetect|PerceptiveStreaming|Microsoft\.ML\.OnnxRuntime|Microsoft\.Windows\.AI\.|Microsoft\.Windows\.Internal\.AI\.|Microsoft\.DiaSymReader|mscordaccore|mscordbi|clretwrc|clrgcexp|System\.Numerics\.Tensors)'
Get-ChildItem $payload -File | Where-Object Name -match $drop | Remove-Item -Force
Remove-Item "$payload\363FastTool.pdb" -Force -ErrorAction SilentlyContinue
Copy-Item "$root\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\platform-tools" $payload -Recurse -Force

# 3) 编译安装包（前置运行库在安装时按需下载）
& $iscc (Join-Path $root 'artifacts\installer\363FastTool.iss') | Select-Object -Last 3
$out = Join-Path $root 'artifacts\installer\out\363FastTool-Setup-1.0.0-x64.exe'
Copy-Item $out (Join-Path $root 'dist\363FastTool-Setup-1.0.0-x64.exe') -Force
Get-Item $out | Select-Object FullName, @{n='MB'; e={[math]::Round($_.Length/1MB,1)}}
Get-FileHash $out -Algorithm SHA256 | Select-Object Hash
