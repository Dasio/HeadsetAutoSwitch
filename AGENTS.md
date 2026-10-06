# AGENTS.md

Guidance for AI coding assistants (Claude Code, Copilot, Cursor, Codex, ...) working in this repository.

## What this is

A Windows tray app that switches the default audio output and microphone when a wireless headset is
turned on or off. A small `.exe`, no installer, no dependencies beyond what ships with Windows.

## Build and test

- `dotnet test tests/HeadsetAutoSwitch.Tests.csproj` and `dotnet build src/HeadsetAutoSwitch.csproj -c Release`
  (.NET SDK 8+). Tests run on macOS/Linux too; on Windows they also run against .NET Framework 4.8.
- The app targets **.NET Framework 4.8** (part of Windows 10/11, nothing to install) with **C# 12**.
  APIs that only exist in modern .NET don't compile (e.g. `Dictionary.GetValueOrDefault`,
  `Marshal`-free `LibraryImport`); records need `src/Polyfills.cs`. Nullable is on and warnings are
  errors (`AnalysisLevel` latest-recommended).
- No NuGet packages in the app; JSON uses `DataContractJsonSerializer`, which exists in both runtimes
  so the parser can be tested on .NET 8.
- Releases: update `CHANGELOG.md` (CI uses the matching section as release notes) and `<Version>` in
  `src/HeadsetAutoSwitch.csproj`, then push a `v*` tag; CI builds the `.exe` and creates the release.

## Layout

| File | Role |
| --- | --- |
| `src/TrayApp.cs` | Tray UI and switching decisions; all state lives on the UI thread |
| `src/AudioSwitcher.cs` | Applies device changes on one worker thread, in request order |
| `src/Audio.cs` | Core Audio COM interop: list endpoints, get/set default device, endpoint notifications |
| `src/IHeadsetMonitor.cs` | Interface for headset sources (connection, battery, presence events) |
| `src/Alpha2Monitor.cs` | HyperX Cloud Alpha 2 Wireless, read directly from the base station's HID interface |
| `src/HeadsetControlMonitor.cs` | Other headsets, by polling `headsetcontrol -b -o json` |
| `src/HeadsetControlOutput.cs`, `src/DeviceName.cs` | Pure logic (JSON parsing, name matching), unit tested |
| `src/Config.cs`, `src/IniFile.cs` | Settings (with validation warnings) and the key = value file format |
| `src/Log.cs`, `src/AppPaths.cs`, `src/Program.cs` | Log, file locations, entry point |
| `tests/` | xUnit tests for the pure logic (parsing, matching, settings) |

`TrayApp` follows the first present monitor in preference order (built-in Alpha 2 first). Monitor
events arrive on background threads and are marshalled to the UI thread before touching state;
device changes are queued on `AudioSwitcher` so a quick off/on is never applied out of order.

## Things that look odd but are deliberate

- **Never set a device that is already the default.** Re-setting the current default still makes
  applications reopen their audio streams, which is an audible gap.
- **Endpoint changes are handled after a 1.5 s quiet period**, not immediately. HyperX NGENUITY
  enables its virtual devices about 0.5 s after its audio engine starts, in a burst of several
  changes; reacting to the first one picks the wrong device.
- **NGENUITY's virtual devices only produce sound while NGENUITY runs.** That's why
  `HeadsetOutputPreferred` is used only while that endpoint is active, with `HeadsetOutput` as the
  fallback.
- **The Alpha 2 monitor blocks on a HID read instead of polling.** The base station pushes
  `FB 0A 01/00` on link changes. When NGENUITY runs it polls the same interface about 75 times a
  second and Windows delivers every input report to every open handle, so the read loop sees a lot
  of unrelated traffic and must ignore it cheaply.
- **The connection state is queried (`52 01`) every time the HID handle is (re)opened**, because the
  push notification only fires on change: at startup, after sleep, after the base station is
  re-plugged.
- **HeadsetControl "off" vs errors:** a headset is off when the battery status is
  `BATTERY_UNAVAILABLE` with HeadsetControl's "offline" error *or with no error at all* (some
  drivers, e.g. Audeze Maxwell, report it that way). Timeouts and other errors must not switch
  anything, and a failed run must not change the state.
- **Settings use `*` wildcards on Windows endpoint names**, not device IDs: IDs change when drivers
  are reinstalled.
- **Two separate states in `TrayApp`:** `headsetOn` is what the headset reports, `routedToHeadset` is
  where the audio was sent (manual switches included). Re-reads at connect/resume are compared with
  `headsetOn` so they never undo a manual switch.
- **Learning the speakers:** a default-device change is remembered as "the speakers" only if the app
  didn't make it, it isn't a headset device, and it didn't happen within 3 s of an endpoint
  appearing/disappearing (that's Windows picking a fallback, e.g. HDMI audio).
- **A switch that can't reach its device is retried** when endpoints change (e.g. monitor speakers
  waking up).

## Conventions

- Match the surrounding style (`.editorconfig`): file-scoped namespaces, short comments that explain *why*.
- Log decisions that change audio devices (tray menu → Open log is how users report problems).
- Keep public docs (README, release notes) factual about what was tested on real hardware.

## Testing on hardware

Unit tests cover parsing and matching only. Changes to switching or device handling need a check on
a real machine: headset off/on, app start with the headset on and off, sleep/resume, and (for the
Cloud Alpha 2) NGENUITY starting and stopping while the headset is on.
