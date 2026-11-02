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

**Installing over a running session fails with error 5.** Inno cannot delete
`platform-tools\AdbWinApi.dll` while an `adb.exe` or `fastboot.exe` from the
previous run still has it loaded, and the installer then aborts mid-copy. Stop
those processes (`Get-Process adb,fastboot | Stop-Process -Force`) before
reinstalling. Both have been hit in practice; a flashed device leaves a `fastboot`
waiting on the USB endpoint long after the app is gone.

**`fastboot.exe` is not Google's signed build.** It is our own build of AOSP
fastboot 36.0.0 carrying MeowArch's progress bar, and it is unsigned; everything
else in that directory is the signed Google Platform-Tools 37.0.1. It is not
reproducible here without MSYS2's mingw-w64 i686 toolchain and an AOSP partial
checkout — read `tools/platform-tools/PROVENANCE.md` before touching any of it.
That file records the exact upstream commits, our four source patches (the
`FORCE_PROGRESS` switch, the MSYS2 binutils fix, the 1 MiB sparse chunking and the
denser pipe cadence), the verified behaviour matrix, and the GPL-3.0 obligation
distribution creates.

The binary draws its bar only when stderr is a terminal, and this app always reads
through a pipe, so both launch sites set `FORCE_PROGRESS=1` on the child
(`FastbootService.FlashImageAsync`, `ROMFlashPageViewModel.ExecuteBatAsync`; the
latter's `cmd.exe` passes it down to every `fastboot flash` inside an OEM script).
Without it a flash is silent and the percent bar never moves.

Keep `AdbWinApi.dll` as the signed Google one. Our fastboot imports 12 functions
from it (`AdbEnumInterfaces`, `AdbCreateInterfaceByName`, `AdbReadEndpointSync`, …)
and the signed DLL exports all 12; both DLL versions export the same 79 functions
at FileVersion 2.0.0.0. So exactly one file in the directory is unsigned, and
swapping the vendor's unsigned DLL in would make it three.

**`<Version>` in `SailTool.csproj` is the single product version.** The packaging
script parses it and passes it to ISCC as `/DMyAppVersion`. `InformationalVersion`
is set explicitly because an explicit `<Version>` overrides the
`VersionPrefix`/`VersionSuffix` composition — using `<VersionSuffix>` would
silently drop the suffix. `IncludeSourceRevisionInInformationalVersion=false`
keeps the SDK from appending `+<sha>` and leaking a hash into the settings page.

`SettingsPageViewModel.ReadAssemblyVersion()` reads that attribute, so the number
in the UI cannot drift from the artifact filename.

## Main window

**The window is pinned at 1552×1063 physical pixels.** `MainWindow`'s constructor
sets it once via `AppWindow.MoveAndResize`; `AppWindow` takes physical pixels, not
DIPs, so the size is unaffected by display scaling. At the system's 125% scaling a
DPI-unaware reader (a plain `GetWindowRect` from PowerShell) reports 1242×850 —
measure with `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)` before deciding
the size is wrong. `OverlappedPresenter.IsResizable = false` and
`IsMaximizable = false` strip `WS_THICKFRAME` and `WS_MAXIMIZEBOX`, so edges cannot
be dragged and the title bar cannot be double-clicked to maximize; minimizing is
still allowed. Launch centers the window in the display's work area (top-left if
the screen is smaller than the window).

**The position is not locked or persisted.** The user can drag the window, and
every launch re-centers it. A window found partly off the right edge (once seen at
x=1306 on the 2560-wide screen) was dragged there; that is not a centering
regression — the launch-time position is the work-area center.

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

## Progress reporting

The shipped `fastboot.exe` draws a transfer progress bar and Google's builds do
not, so there are two code paths and the UI has to say which one it is on. The bar
is off by default when stderr is a pipe — which is always, here — so the launch
sites also set `FORCE_PROGRESS=1`.

`FastbootCapability.SupportsTransferProgress(path)` decides by scanning the binary
for ASCII needles: `%3d%%` (the bar's percentage format) or `(%.1f%%)` (the
previous binary's transfer-line format). Any hit means progress is available. That
is a property of the build, not something the stream reveals.
`FastbootService.SupportsTransferProgress` exposes it and `FlashPageViewModel`
writes a hint line into the log panel when it is false.

The hint goes to the log, not to a new control. The page layout is deliberately
untouched: everything the user sees during a flash uses the percent bar and the
speed label that were already there. What changed is what those two now report and
what the log panel prints.

The log panel keeps a transfer on **one line**: a progress frame overwrites the
previous frame instead of being appended, so the panel shows 0% → 100% in place
rather than thousands of lines. That is `LogBuffer` (pure string surgery) plus
`LogWriter.AppendProgress`, and the two call sites decide frame-vs-message with
`FastbootOutputParser.IsProgressLine`. The decision to overwrite is taken from the
buffer's own last line, on the UI thread, because stdout and stderr are two
readers on two threads — a flag maintained by the callers would race.

The stored frame is **not** the raw frame: `FastbootOutputParser.RemoveProgressBar`
drops the `[####>----]` graphic and the padding in front of it, because the page
already has a real progress bar above the log and the drawn one is the same
information a second time. That is why `IsProgressLine` also matches
`StrippedBarFrameRegex` (`NN%  …MiB/…MiB` with no bar) — the buffer classifies
its own stored text, and a stripped frame that stopped classifying would append
again and bring the flood back. The speed is likewise printed once, on the label
to the right of the bar; `StatusText` carries only `正在刷写...`, so a narrow
window never truncates a number that is already on screen.

What the log gate samples is exactly what the binary redraws: bar frames, the old
transfer lines, and sparse chunk headers. Two lines are deliberately never
sampled. A **result** line is not, even when it parses as the frame it was
appended to — fastboot writes `OKAY` onto the line it was drawing on, so
`Sending 'boot' (19531 KB)   OKAY [  1.272s]` and the sparse
`... 13/13 (262040 KB)   OKAY` both match a progress pattern, and dropping either
hides the line that says the transfer worked. The **100% frame** is not, because
it is where the one log line comes to rest and it arrives tens of milliseconds
after the last sampled frame (98%→100% at 40 MiB/s is ~70 ms). Both were real
regressions once, and both are pinned in `FastbootBarProgressTests`.

The pipeline for one flash is:

```
child process streams
  → FastbootProcessService.ReadStreamLinesAsync   splits on \r AND \n, decodes via StreamReader
  → ProgressLogGate.ShouldLog                     samples progress lines for the log panel
    → LogWriter.Append / AppendProgress           append a message, or overwrite the frame
  → FlashProgressTracker.HandleLine               state machine, monotonic cursors
  → ProgressThrottle.ShouldRender                 samples updates for the progress bar
  → FlashPageViewModel progressReporter           DispatcherQueue hop, then the XAML labels
```

Both gates are 100 ms, drop identical repeats, and pass terminal states straight
through. They are separate objects because they gate different things: the log
gates *lines*, the throttle gates *progress updates*, and the tracker must still
see every line or its cursors lose data.

The 100 ms figure and the "drop unchanged state" rule are a product decision from
the owner, not a tuned constant — do not retune them without asking. It was 300 ms
until 2026-11-01; at that spacing a 40 MiB/s transfer visibly stepped.

Two of our patches to fastboot exist for that 100 ms window, and removing either
brings the stutter back. `SparseWriteCallback` sends a sparse download in 1 MiB
pieces instead of one `SendBuffer` per sparse span — a span runs to tens of MiB and
the progress callback hangs off `SendBuffer`, so the bar stood still for a second
and then jumped. And with `FORCE_PROGRESS=1` the bar's own cadence drops from
80 ms/`total÷400` to 25 ms/`total÷2000`, because an 80 ms source sampled at 100 ms
beats into uneven 80/160 ms gaps — two clocks that are nearly, but not exactly,
in step. Measured with the TCP mock at 15 MB/s: 67 ms gaps on both paths, 97 frames
for a 100 MiB sparse flash that used to emit 8.

`ROMFlashPageViewModel.ExecuteBatAsync` reuses `ReadStreamLinesAsync` and
`ProgressLogGate` but not the tracker. It injects `platform-tools` into the child
`PATH`, so every `fastboot flash` inside an OEM script emits progress lines too —
reading it with `BeginOutputReadLine` would buffer the whole transfer and then dump
thousands of lines at once.

Two things that look wrong but are deliberate:

- The transfer-progress pattern's partition group is `.+?`, not `[^:]+?`. A lazy
  character class cannot span the colon in `vendor_boot:RAMDISK`; backtracking past
  it is what makes that target work.
- `ProgressPercentRegex` tolerates whitespace inside the parens even though the
  recorded sample output has none — the size field before it is right-aligned
  (`recovery:   5.0 MB` vs `recovery: 100.0 MB`), the percentage is not. The `\s*`
  is defensive, not a claim about this binary.
- `FlashProgressTracker` clears the native rate only when the `Sending` header's
  partition-and-size changes, not on every `Sending` match. Every bar frame repeats
  that prolog, so clearing unconditionally would wipe the rate immediately after
  each frame that carried one, and the speed label would flicker.

## Known defects — documented and pinned, not fixed

`AdbDeviceLineRegex`'s serial class `[A-Za-z0-9_-]+` excludes dots and colons, so a
device over `adb tcpip` (`192.168.1.50:5555`) never reaches the list. USB serials,
including ones with dashes and underscores, and the `emulator-5554` form are fine.
The pattern's XML doc records this and `FastbootOutputParserTests` pins it. Widening
the class changes which devices the app offers to flash, so it needs an explicit
decision and a device to test against.

A bootloader percentage pushed as `(bootloader) 47%` is now parsed — it was not
before, and the old doc claimed it was. That was a genuine bug, fixed along with
the progress work.

`ROMFlashPageViewModel.ExecuteBatAsync` builds its own `Process` instead of going
through `FastbootProcessService`. It shares the stream reader and the log gate, so
the remaining divergence is process lifetime and cancellation, not parsing.

The 线刷 page has a percentage bar that never moves. `OverallProgress` is declared
and reset to 0 at the start of a flash but never advanced anywhere, so
`HasExactProgress` stays false and `ROMFlashPage.xaml.cs` shows the indeterminate
ring for the whole run. This predates the progress work — the page has never
reported a percentage. Implementing it means the appendix-A approach: pre-scan the
OEM script for every `fastboot flash <part> <img>`, sum the image sizes, then
accumulate per partition as progress lines name them. That needs a decision about
how to handle scripts whose partition list is not knowable up front, and about
whether to show a total-GB figure the user cannot verify.

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
