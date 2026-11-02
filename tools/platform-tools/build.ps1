#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the platform-tools set from source into artifacts/platform-tools.

.DESCRIPTION
    Nothing binary is checked into tools/platform-tools: every executable this
    script writes is compiled here from pinned upstream sources. The sources are
    cloned into artifacts/tool-cache, patched with the files under
    tools/platform-tools/patches, and built with MSYS2 toolchains.

        adb         prife/adb (AOSP adb 1.0.40) + BoringSSL 8.1 (sources.mk)
        fastboot    MeowArchMobile_Android_Tools (AOSP fastboot + MeowArch UX)
        magiskboot  svoboda18/android_tools (Magisk native sources)

    Requirements (see README.md for the exact pacman command):
        MSYS2 with mingw-w64-i686 (gcc, cmake, ninja, ar, dlltool)
        MSYS2 CLANG64 with clang, libc++, make, xz, lz4, bzip2, zlib
        git, and PowerShell 7

.PARAMETER Msys2Root
    MSYS2 installation. Defaults to $env:MSYS2_ROOT, C:\msys64, or
    %USERPROFILE%\toolchain\msys64, whichever exists.

.PARAMETER CacheDir
    Where upstream checkouts live. Defaults to artifacts/tool-cache.

.PARAMETER OutputDir
    Where the built files go. Defaults to artifacts/platform-tools.

.PARAMETER Force
    Ignore the output stamp and rebuild everything.

.PARAMETER SkipAdb / SkipFastboot / SkipMagiskBoot
    Build only the other components (the output directory keeps whatever was
    there before for the skipped one).
#>
[CmdletBinding()]
param(
    [string]$Msys2Root = '',
    [string]$CacheDir = '',
    [string]$OutputDir = '',
    [switch]$Force,
    [switch]$SkipAdb,
    [switch]$SkipFastboot,
    [switch]$SkipMagiskBoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = $PSScriptRoot
$root      = Split-Path -Parent (Split-Path -Parent $scriptDir)
if (-not $CacheDir)  { $CacheDir  = Join-Path $root 'artifacts\tool-cache' }
if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts\platform-tools' }
$CacheDir  = [IO.Path]::GetFullPath($CacheDir)
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$stampPath = Join-Path $OutputDir 'build-info.json'
Set-Location $root

function Step([string]$Message) { Write-Host "==> $Message" }
function Info([string]$Message) { Write-Host "    $Message" }

function Invoke-Checked {
    param([string]$Exe, [string[]]$Arguments, [string]$WorkingDirectory = '')
    if ($WorkingDirectory) { Push-Location $WorkingDirectory }
    try {
        & $Exe @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "command failed (exit $LASTEXITCODE): $Exe $($Arguments -join ' ')"
        }
    } finally {
        if ($WorkingDirectory) { Pop-Location }
    }
}

function Resolve-Msys2Root {
    param([string]$Explicit)
    $candidates = @()
    if ($Explicit)        { $candidates += $Explicit }
    if ($env:MSYS2_ROOT)  { $candidates += $env:MSYS2_ROOT }
    $candidates += 'C:\msys64'
    $candidates += (Join-Path $env:USERPROFILE 'toolchain\msys64')
    $candidates += 'C:\tools\msys64'
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'usr\bin\bash.exe'))) {
            return (Resolve-Path $candidate).Path
        }
    }
    throw "MSYS2 not found. Pass -Msys2Root or set MSYS2_ROOT (e.g. $env:USERPROFILE\toolchain\msys64)."
}

function Ensure-Checkout {
    param([string]$Repo, [string]$Commit, [string]$Dir, [string]$Label)
    if (Test-Path (Join-Path $Dir '.git')) {
        $head = (& git -C $Dir rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and $head.Trim() -eq $Commit) {
            Info "${Label}: $($Commit.Substring(0,12)) (cached)"
            return
        }
        Info "${Label}: refreshing $($Commit.Substring(0,12))"
        Set-Location $root
        Remove-Item $Dir -Recurse -Force
    } else {
        Info "${Label}: fetching $($Commit.Substring(0,12))"
    }
    New-Item -ItemType Directory -Force (Split-Path $Dir -Parent) | Out-Null
    try {
        Invoke-Checked git @('init','-q',$Dir)
        Invoke-Checked git @('-C',$Dir,'remote','add','origin',$Repo)
        Invoke-Checked git @('-C',$Dir,'fetch','-q','--depth','1','origin',$Commit)
        Invoke-Checked git @('-C',$Dir,'checkout','-q','--detach','FETCH_HEAD')
    } catch {
        Remove-Item $Dir -Recurse -Force -ErrorAction SilentlyContinue
        Invoke-Checked git @('clone','-q',$Repo,$Dir)
        Invoke-Checked git @('-C',$Dir,'checkout','-q',$Commit)
    }
}

function Apply-Patch {
    param([string]$Patch, [string]$Dir, [string[]]$Extra = @())
    $check = @('-C',$Dir,'apply','--check','--ignore-whitespace','--whitespace=nowarn') + $Extra + @($Patch)
    & git @check 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $apply = @('-C',$Dir,'apply','--ignore-whitespace','--whitespace=nowarn') + $Extra + @($Patch)
        Invoke-Checked git $apply
        Info "patched: $(Split-Path $Patch -Leaf)"
        return
    }
    $reverse = @('-C',$Dir,'apply','--reverse','--check','--ignore-whitespace','--whitespace=nowarn') + $Extra + @($Patch)
    & git @reverse 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "patch neither applies nor is already applied: $Patch"
    }
    Info "already applied: $(Split-Path $Patch -Leaf)"
}

# ------------------------------------------------------------------ setup
$Msys2Root = Resolve-Msys2Root $Msys2Root
$mingw     = Join-Path $Msys2Root 'mingw32\bin'
$clang64   = Join-Path $Msys2Root 'clang64\bin'
$msysBash  = Join-Path $Msys2Root 'usr\bin\bash.exe'
$gcc       = Join-Path $mingw 'gcc.exe'
$gpp       = Join-Path $mingw 'g++.exe'
$cmake     = Join-Path $mingw 'cmake.exe'
$ar        = Join-Path $mingw 'ar.exe'
$strip     = Join-Path $mingw 'strip.exe'
$dlltool   = Join-Path $mingw 'dlltool.exe'
$clang     = Join-Path $clang64 'clang.exe'

foreach ($tool in @($gcc,$gpp,$cmake,$ar,$strip,$dlltool,$msysBash,$clang)) {
    if (-not (Test-Path $tool)) {
        throw @"
Missing build tool: $tool

Install the MSYS2 packages first (from an MSYS2 shell):

  pacman -S --needed base-devel patch ^
    mingw-w64-i686-gcc mingw-w64-i686-cmake mingw-w64-i686-ninja ^
    mingw-w64-clang-x86_64-clang mingw-w64-clang-x86_64-libc++ ^
    mingw-w64-clang-x86_64-make mingw-w64-clang-x86_64-xz ^
    mingw-w64-clang-x86_64-lz4 mingw-w64-clang-x86_64-bzip2 ^
    mingw-w64-clang-x86_64-zlib mingw-w64-clang-x86_64-pkgconf
"@
    }
}

$env:PATH = "$mingw;$clang64;$env:PATH"
New-Item -ItemType Directory -Force $CacheDir  | Out-Null
New-Item -ItemType Directory -Force $OutputDir | Out-Null

# MSYS2's own /usr/bin has no git or patch; put the Windows Git directory on
# the bash side of the fence (in its /c/... spelling) for fetch-aosp.sh.
$gitDir     = (Split-Path (Get-Command git).Source -Parent).Replace('\','/')
$gitDirUnix = ($gitDir -replace '^([A-Za-z]):', '/$1')

$sources  = Get-Content (Join-Path $scriptDir 'sources.json') -Raw | ConvertFrom-Json
$patchDir = Join-Path $scriptDir 'patches'

$required = @('adb.exe','fastboot.exe','magiskboot.exe','AdbWinApi.dll','AdbWinUsbApi.dll')
if (-not $Force -and (Test-Path $stampPath)) {
    try {
        $previous = Get-Content $stampPath -Raw | ConvertFrom-Json
        $sameSources = ($previous.sources.adb -eq $sources.adb.commit) -and
                       ($previous.sources.fastboot -eq $sources.fastboot.commit) -and
                       ($previous.sources.magiskboot -eq $sources.magiskboot.commit) -and
                       ($previous.sources.boringssl -eq $sources.boringssl.commit)
        $allPresent = -not ($required | Where-Object { -not (Test-Path (Join-Path $OutputDir $_)) })
        if ($sameSources -and $allPresent) {
            Step "output is current ($OutputDir); use -Force to rebuild"
            exit 0
        }
    } catch {
        # An older or partial stamp: fall through and rebuild.
    }
}

$stopwatch = [Diagnostics.Stopwatch]::StartNew()

# ------------------------------------------------------- ADB dependencies
if (-not $SkipAdb) {
    Step 'adb: BoringSSL 8.1 from source'
    $bsslDir   = Join-Path $CacheDir 'boringssl'
    $bsslBuild = Join-Path $CacheDir 'boringssl-build'
    Ensure-Checkout $sources.boringssl.repo $sources.boringssl.commit $bsslDir 'boringssl'

    $sourcesMk = Join-Path $bsslDir 'sources.mk'
    $text = Get-Content $sourcesMk -Raw
    $match = [regex]::Match($text, '(?ms)^crypto_sources :=(.*?)(?=^\w+_sources :=|\z)')
    if (-not $match.Success) { throw "crypto_sources not found in $sourcesMk" }
    $cryptoSources = foreach ($line in ($match.Groups[1].Value -split "`r?`n")) {
        $entry = $line.Trim()
        if ($entry.EndsWith('\')) { $entry = $entry.Substring(0, $entry.Length - 1).Trim() }
        if ($entry) { $entry }
    }
    Info "$($cryptoSources.Count) translation units"

    $objDir = Join-Path $bsslBuild 'obj'
    if ($Force -and (Test-Path $objDir)) { Remove-Item $objDir -Recurse -Force }
    New-Item -ItemType Directory -Force $objDir | Out-Null
    $include = @(
        ('-I' + (Join-Path $bsslDir 'src\include')),
        ('-I' + (Join-Path $bsslDir 'src\crypto'))
    )
    foreach ($source in $cryptoSources) {
        $obj = Join-Path $objDir (($source -replace '[\\/]', '_') + '.o')
        $flags = @('-c','-O2','-DOPENSSL_NO_ASM','-DBORINGSSL_IMPLEMENTATION','-DOPENSSL_SMALL') + $include
        if ($source -like '*cpu-intel.c') { $flags += '-mxsave' }
        Invoke-Checked $gcc ($flags + @((Join-Path $bsslDir $source), '-o', $obj))
    }
    $libcrypto = Join-Path $bsslBuild 'libcrypto.a'
    Remove-Item $libcrypto -ErrorAction SilentlyContinue
    $objects = Get-ChildItem $objDir -Filter *.o | ForEach-Object FullName
    for ($i = 0; $i -lt $objects.Count; $i += 80) {
        $chunk = $objects[$i..([Math]::Min($i + 79, $objects.Count - 1))]
        Invoke-Checked $ar (@('rcs', $libcrypto) + $chunk)
    }
    Info "libcrypto.a: $((Get-Item $libcrypto).Length) bytes"

    Step 'adb: AOSP adb 1.0.40 (prife/adb)'
    $adbSrc   = Join-Path $CacheDir 'adb-src'
    $adbBuild = Join-Path $CacheDir 'adb-build'
    Ensure-Checkout $sources.adb.repo $sources.adb.commit $adbSrc 'adb'

    # prife/adb's CMake expects BoringSSL at lib/boringssl; point it at the
    # shared cache instead of cloning the 300 MB tree a second time. Without
    # this the sources pick up the system OpenSSL 3 headers and fail on the
    # opaque RSA struct.
    $bsslLink = Join-Path $adbSrc 'lib\boringssl'
    if (-not (Test-Path $bsslLink)) {
        New-Item -ItemType Junction -Path $bsslLink -Target $bsslDir | Out-Null
    }

    foreach ($patch in Get-ChildItem (Join-Path $patchDir 'adb') -Filter *.patch | Sort-Object Name) {
        Apply-Patch $patch.FullName $adbSrc
    }

    # The checkout expects a prebuilt libcrypto.a; replace it with the one
    # this script just compiled, and with our source-built DLL import libs.
    Copy-Item $libcrypto (Join-Path $adbSrc 'prebuilt\windows\32\libcrypto.a') -Force

    Step 'adb: AdbWinApi.dll / AdbWinUsbApi.dll from AOSP sources'
    $usbSrc     = Join-Path $adbSrc 'lib\windows\usb'
    $apiSrc     = Join-Path $usbSrc 'api'
    $winUsbSrc  = Join-Path $usbSrc 'winusb'
    $compat     = Join-Path $usbSrc 'compat'
    $winApiDll  = Join-Path $OutputDir 'AdbWinApi.dll'
    $winApiImpl = Join-Path $OutputDir 'libAdbWinApi.a'
    $winUsbDll  = Join-Path $OutputDir 'AdbWinUsbApi.dll'
    $winUsbImpl = Join-Path $OutputDir 'libAdbWinUsbApi.a'

    $commonDefs = @('-D_UNICODE','-DUNICODE','-DWIN32','-D_WINDOWS','-D_USRDLL','-D_WINDLL','-DNDEBUG')
    $apiCpp = Get-ChildItem $apiSrc -Filter *.cpp | ForEach-Object FullName
    Invoke-Checked $gpp (@('-shared','-o',$winApiDll) + $apiCpp + @(
        (Join-Path $apiSrc 'AdbWinApi.rc'), '-I',$apiSrc, '-I',$compat, '-DADBWIN_EXPORTS') +
        $commonDefs + @("-Wl,--out-implib,$winApiImpl",
        '-lole32','-loleaut32','-luuid','-lsetupapi','-ladvapi32','-luser32',
        '-static-libgcc','-static-libstdc++','-static'))

    # WinUSB import library. The definitions carry their stdcall decoration,
    # so dlltool is invoked without -k.
    $winUsbDef = Join-Path $CacheDir 'winusb.def'
    @'
LIBRARY winusb.dll
EXPORTS
WinUsb_Initialize@8
WinUsb_Free@4
WinUsb_GetDescriptor@28
WinUsb_QueryInterfaceSettings@12
WinUsb_QueryPipe@16
WinUsb_SetPipePolicy@20
WinUsb_ResetPipe@8
WinUsb_AbortPipe@8
WinUsb_ReadPipe@24
WinUsb_WritePipe@24
WinUsb_GetOverlappedResult@16
WinUsb_GetCurrentAlternateSetting@8
'@ | Set-Content -Path $winUsbDef -Encoding ascii
    $winUsbLib = Join-Path $CacheDir 'libwinusb.a'
    Invoke-Checked $dlltool @('-d',$winUsbDef,'-l',$winUsbLib)

    $winUsbCpp = Get-ChildItem $winUsbSrc -Filter *.cpp | ForEach-Object FullName
    Invoke-Checked $gpp (@('-shared','-o',$winUsbDll) + $winUsbCpp + @(
        (Join-Path $winUsbSrc 'AdbWinUsbApi.rc'), '-I',$winUsbSrc, '-I',$usbSrc,
        '-I',$apiSrc, '-I',$compat, '-DADBWINUSB_EXPORTS') + $commonDefs + @(
        $winUsbLib, $winApiImpl, "-Wl,--out-implib,$winUsbImpl",
        '-lole32','-loleaut32','-luuid','-lsetupapi','-ladvapi32','-luser32',
        '-static-libgcc','-static-libstdc++','-static'))

    # prife/adb links -lAdbWinApi / -lAdbWinUsbApi from its prebuilt directory.
    $prebuilt = Join-Path $usbSrc 'prebuilt\usb'
    New-Item -ItemType Directory -Force $prebuilt | Out-Null
    Copy-Item $winApiImpl (Join-Path $prebuilt 'libAdbWinApi.a') -Force
    Copy-Item $winUsbImpl (Join-Path $prebuilt 'libAdbWinUsbApi.a') -Force

    Step 'adb: compiling'
    if ($Force) { Remove-Item $adbBuild -Recurse -Force -ErrorAction SilentlyContinue }
    Invoke-Checked $cmake @('-S',$adbSrc,'-B',$adbBuild,'-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DCMAKE_EXE_LINKER_FLAGS=-static')
    Invoke-Checked $cmake @('--build',$adbBuild)
    Copy-Item (Join-Path $adbBuild 'src\adb.exe') (Join-Path $OutputDir 'adb.exe') -Force
    Info "adb.exe: $((Get-Item (Join-Path $OutputDir 'adb.exe')).Length) bytes"
}

# ------------------------------------------------------------- fastboot
if (-not $SkipFastboot) {
    Step 'fastboot: MeowArchMobile_Android_Tools'
    $fbSrc   = Join-Path $CacheDir 'meowarch-src'
    $fbBuild = Join-Path $CacheDir 'meowarch-build'
    $aospDir = Join-Path $CacheDir 'aosp-adbd'
    Ensure-Checkout $sources.fastboot.repo $sources.fastboot.commit $fbSrc 'fastboot'
    foreach ($patch in Get-ChildItem (Join-Path $patchDir 'fastboot') -Filter *.patch | Sort-Object Name) {
        Apply-Patch $patch.FullName $fbSrc
    }

    Step 'fastboot: AOSP snippet (fetch-aosp.sh)'
    $fetch = Join-Path $fbSrc 'better_fastboot\scripts\fetch-aosp.sh'
    $unixFetch = $fetch.Replace('\','/')
    $unixAosp  = $aospDir.Replace('\','/')
    $unixFb   = (Join-Path $fbSrc 'better_fastboot').Replace('\','/')
    Invoke-Checked $msysBash @('-c', "export PATH='$gitDirUnix':/usr/bin:/mingw32/bin:`$PATH; export AOSP_ROOT_DIR='$unixAosp'; cd '$unixFb' && ./scripts/fetch-aosp.sh")

    # fastboot links and ships Google's prebuilt AdbWinApi. Replace both with
    # the DLL and import library this script compiled from AOSP sources.
    $winPrebuilt = Join-Path $fbSrc 'better_fastboot\win-prebuilt\usb'
    Copy-Item (Join-Path $OutputDir 'libAdbWinApi.a') (Join-Path $winPrebuilt 'AdbWinApi.a') -Force
    Copy-Item (Join-Path $OutputDir 'AdbWinApi.dll')    (Join-Path $winPrebuilt 'AdbWinApi.dll') -Force
    Copy-Item (Join-Path $OutputDir 'AdbWinUsbApi.dll') (Join-Path $winPrebuilt 'AdbWinUsbApi.dll') -Force

    Step 'fastboot: compiling'
    if ($Force) { Remove-Item $fbBuild -Recurse -Force -ErrorAction SilentlyContinue }
    $fbSub = Join-Path $fbSrc 'better_fastboot'
    Invoke-Checked $cmake @('-S',$fbSub,'-B',$fbBuild,'-G','Ninja',
        ('-DCMAKE_TOOLCHAIN_FILE=' + (Join-Path $fbSub 'cmake\mingw-i686.cmake')),
        '-DCMAKE_BUILD_TYPE=Release', ('-DAOSP_ROOT=' + $aospDir))
    Invoke-Checked $cmake @('--build',$fbBuild)
    $fbExe = Join-Path $fbBuild 'fastboot.exe'
    Invoke-Checked $strip @('-s',$fbExe)
    Copy-Item $fbExe (Join-Path $OutputDir 'fastboot.exe') -Force
    Info "fastboot.exe: $((Get-Item (Join-Path $OutputDir 'fastboot.exe')).Length) bytes"

    foreach ($extra in @('mke2fs.exe','make_f2fs.exe','make_f2fs_casefold.exe','mke2fs.conf')) {
        $candidate = Join-Path $fbBuild $extra
        if (Test-Path $candidate) { Copy-Item $candidate (Join-Path $OutputDir $extra) -Force }
    }
}

# ----------------------------------------------------------- magiskboot
if (-not $SkipMagiskBoot) {
    Step 'magiskboot: svoboda18/android_tools (Magisk native sources)'
    $mbSrc   = Join-Path $CacheDir 'magiskboot-src'
    Ensure-Checkout $sources.magiskboot.repo $sources.magiskboot.commit $mbSrc 'magiskboot'
    if (-not (Test-Path (Join-Path $mbSrc 'boot\bootimg.cpp'))) {
        Info 'initializing submodules'
        Invoke-Checked git @('-C',$mbSrc,'submodule','update','--init','--recursive')
    }
    foreach ($patch in Get-ChildItem (Join-Path $patchDir 'magiskboot') -Filter *.patch | Sort-Object Name) {
        Apply-Patch $patch.FullName (Join-Path $mbSrc 'libnt')
    }

    Step 'magiskboot: compiling (CLANG64)'
    if ($Force) { Remove-Item (Join-Path $mbSrc 'out') -Recurse -Force -ErrorAction SilentlyContinue }
    $mbUnix = $mbSrc.Replace('\','/')
    $env:MSYSTEM = 'CLANG64'
    try {
        Invoke-Checked $msysBash @('-lc', "cd '$mbUnix' && mingw32-make")
    } finally {
        Remove-Item Env:MSYSTEM -ErrorAction SilentlyContinue
    }
    Copy-Item (Join-Path $mbSrc 'out\magiskboot.exe') (Join-Path $OutputDir 'magiskboot.exe') -Force
    Info "magiskboot.exe: $((Get-Item (Join-Path $OutputDir 'magiskboot.exe')).Length) bytes"
}

# --------------------------------------------------------------- stamp
$files = @{}
foreach ($name in $required) {
    $path = Join-Path $OutputDir $name
    if (Test-Path $path) {
        $files[$name] = @{
            size   = (Get-Item $path).Length
            sha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

@"
Package: SailTool platform-tools (built from source)
Pkg.Revision: 1.0.0
adb.version: $($sources.adb.version)
fastboot.version: $($sources.fastboot.version)
adb.source: $($sources.adb.repo)@$($sources.adb.commit)
boringssl.source: $($sources.boringssl.repo)#$($sources.boringssl.ref)
fastboot.source: $($sources.fastboot.repo)@$($sources.fastboot.commit)
magiskboot.source: $($sources.magiskboot.repo)@$($sources.magiskboot.commit)
"@ | Set-Content -Path (Join-Path $OutputDir 'source.properties') -Encoding ascii

$info = [ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    msys2Root   = $Msys2Root
    sources     = [ordered]@{
        adb        = $sources.adb.commit
        boringssl  = $sources.boringssl.commit
        fastboot   = $sources.fastboot.commit
        magiskboot = $sources.magiskboot.commit
    }
    files       = $files
}
$info | ConvertTo-Json -Depth 6 | Set-Content -Path $stampPath -Encoding utf8

$stopwatch.Stop()
Step "done in $([math]::Round($stopwatch.Elapsed.TotalSeconds,1)) s -> $OutputDir"
Get-ChildItem $OutputDir | Select-Object Name, @{n='KiB'; e={[math]::Round($_.Length/1KB,1)}} | Format-Table -AutoSize | Out-String | Write-Host
