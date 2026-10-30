# CLAUDE.md

Guidance for working in this repository. Read this before changing anything —
several constraints below are non-obvious and were each found the hard way.

## What this is

SailTool (renamed from 363FastTool) is an unpackaged WinUI 3 desktop app for
flashing Android devices over fastboot and adb. It shells out to
`adb.exe` / `fastboot.exe` / `magiskboot.exe` from `tools/platform-tools`.

The UI is Chinese. That is the product's language and is not a translation gap.

## Commands

The dotnet on PATH is `C:\Program Files\dotnet`, which holds runtimes only and no
SDK — building with it fails with NETSDK1045. The usable SDK 10.0.403 lives in
`%USERPROFILE%\.dotnet`.

```bash
"$USERPROFILE/.dotnet/dotnet.exe" build SailTool.sln
"$USERPROFILE/.dotnet/dotnet.exe" test tests/SailTool.Tests/SailTool.Tests.csproj
pwsh -File installer/build-setup.ps1        # -> dist/SailTool-Setup-<version>-x64.exe
```

Inno Setup 6 is at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`. Both tool
paths are `-DotNetExe` / `-IsccExe` parameters on the packaging script.

Run these from the repo root. The solution builds `Debug|x64` by default because
`SailTool.csproj` computes `RuntimeIdentifier` from the process architecture.

## Constraints that break the build if violated

**`CsWinRTAotOptimizerEnabled` must stay `false`.** Re-tested on WindowsAppSDK
2.5.1: enabling it produces 60 errors. The WinRT source generator emits
`WinRTExposedTypeAttribute` with three arguments where fewer exist, mangles the
`<RootNamespace>VtableClasses` identifier, and then the XAML compiler itself dies
with WMC9999. Do not "clean this up".

**`WindowsPackageType` stays `None`.** The app ships unpackaged; the installer
handles distribution. `EnableMsixTooling` is on only so `Package.appxmanifest`
keeps being validated, and that manifest's `Version` is the 4-part MSIX identity
version — deliberately *not* the product version.

**`tools/platform-tools` is the source of truth for the binaries**, and it is
tracked in git. `SailTool.csproj` bridges it into the output with `None` items
(`Link="platform-tools\..."`), not `Content` — Content items get picked up by the
WinUI resource indexer and break the build. `PatchService` finds `magiskboot.exe`
next to `fastboot.exe`, in `AppContext.BaseDirectory`, or in the `platform-tools`
subfolder; `FastbootProcessService.FindToolInPlatformTools` looks beside the
executable first and then on the user's Desktop (a dev convenience), returning
null otherwise — it does not search PATH despite the comment numbering implying a
third location.

Never copy these binaries into `bin/`. An earlier version of the packaging script
copied `platform-tools` from a gitignored Debug output tree, so `dotnet clean`
destroyed the only copy.

**`<Version>` in `SailTool.csproj` is the single product version.** The packaging
script parses it and passes it to ISCC as `/DMyAppVersion`. `InformationalVersion`
is set explicitly because an explicit `<Version>` overrides the
`VersionPrefix`/`VersionSuffix` composition — using `<VersionSuffix>` would
silently drop the suffix. `IncludeSourceRevisionInInformationalVersion=false`
keeps the SDK from appending `+<sha>` and leaking a hash into the settings page.

`SettingsPageViewModel.ReadAssemblyVersion()` reads that attribute, so the number
in the UI cannot drift from the artifact filename.

## Where things live

```
src/SailTool/           the app
  Enums/                AdbMode, ConnectionType, BootloaderStatus + display-text
                        extensions (the Chinese labels live in the extensions)
  Models/               DeviceInfo, DetailedDeviceInfo, FlashProgressInfo (records)
  Services/             process + device + flash orchestration
    Parsing/            pure text parsing, internal, no WinUI types
  ViewModels/           CommunityToolkit.Mvvm ObservableObject + [RelayCommand]
  Pages/                WinUI pages; code-behind drives controls imperatively
  Helpers/              DialogHelper, FilePickerHelper, FileUtils, LogWriter
tests/SailTool.Tests/   xUnit, mirrors the src layout
installer/              Inno Setup script + packaging + bootstrap test scripts
tools/platform-tools/   adb, fastboot, magiskboot, and the rest (tracked)
```

The dependency chain inside `Services/` is fixed:
`FastbootProcessService` owns child processes →
`DeviceDetectionService` reads device state through it →
`FastbootService` re-exposes both and adds flashing.
`FastbootServiceAccessor` builds the three-stage `Lazy<>` chain and caches the
ViewModels, which is what keeps page state across navigation.

## Testing rules

`SailTool.Tests.csproj` pins `RuntimeIdentifier` to `win-x64` and declares
`Platforms=x64` only. The RID is a global property, so leaving it unset would
make the app project build for `win-arm64` on an ARM host while the test host ran
`win-x64`.

Tests reach internal types via `<InternalsVisibleTo Include="SailTool.Tests"/>`
in `SailTool.csproj`. Keep anything new in `Services/Parsing/` free of WinUI
types in its signature — the parsing layer is the only part that can be loaded
outside a WinUI host, and it is what makes it testable at all.

Nothing in the tests starts the app. `App.DispatcherQueue` is unavailable in the
test host, so `Helpers/LogWriter` and the ViewModels cannot be exercised there;
that is why the parsing and the progress state machine were extracted as pure
classes.

## Known defects — documented and pinned, not fixed

Two real pre-existing bugs are recorded in the XML docs on their patterns in
`Services/Parsing/FastbootOutputParser.cs`, each with a test asserting the current
behaviour so the gap stays visible:

1. `ProgressPercentRegex` is `\((\d{1,3})%`, but fastboot relays bootloader INFO
   text as `(bootloader) %s` — verified as the only such format string in the
   shipped binary. A bootloader pushing `5%` arrives as `(bootloader) 5%` and does
   not match, so the bar stays indeterminate on devices that do report progress.
2. `AdbDeviceLineRegex`'s serial class `[A-Za-z0-9_-]+` excludes dots and colons,
   so a device over `adb tcpip` (`192.168.1.50:5555`) never reaches the list.

Fixing either changes behaviour on real hardware. That needs an explicit decision
and a hardware test, not a drive-by edit. Same for
`FastbootOutputParser.ProgressSpeedRegex`: stock fastboot emits no percentage and
no rate (verified by string-extracting both Platform-Tools 37.0.0 and 37.0.1), so
that branch is dormant and exists for vendor builds.

`ROMFlashPageViewModel.ExecuteBatAsync` builds its own `Process` instead of going
through `FastbootProcessService`. Left alone deliberately.

## Language conventions

User-visible strings are Chinese and stay Chinese. XML doc comments are English.
Inline `//` comments are Chinese, matching the surrounding code — they annotate
local control flow next to Chinese status strings, and translating them would
churn every page for no contract gain.

When a string is displayed to the user, treat it as UI. `tests/` pin the exact
Chinese labels for bootloader state and detection type, because those strings are
what the user reads; a wording change there should fail a test.

## Version control

The `pre-refactor` tag marks the last commit before the October 2026 restructure.
Every step since is its own commit, each independently revertable, and each
verified with a clean build before committing.

`artifacts/` and `dist/` are ignored and regenerable via
`installer/build-setup.ps1`. `bin/` and `obj/` are ignored at any depth. Do not
commit build output — check `git status` for untracked `bin/`/`obj/` before
staging with `git add -A`.
