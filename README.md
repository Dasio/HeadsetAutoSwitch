# HeadsetAutoSwitch

Switches Windows' default speakers and microphone when you **turn a wireless headset on or off**,
and back when you turn it off.

## Why

Wireless headsets with a USB dongle or base station have a problem: the dongle stays plugged in,
so Windows keeps listing the headset as available even when it's switched off. Windows never
switches back to your speakers, and switching by hand (or with a hotkey tool) gets old fast.

HeadsetAutoSwitch asks the headset itself whether it's on, and switches for you.

## Supported headsets

| Headset | How it works | Status |
| --- | --- | --- |
| HyperX Cloud Alpha 2 Wireless | Built in: the base station reports the headset going on/off instantly | Tested |
| Many others (SteelSeries, Logitech, Corsair, Audeze, ...) | Through [HeadsetControl](https://github.com/Sapd/HeadsetControl): checks every few seconds whether the headset answers | Experimental |

For HeadsetControl, your headset needs **battery** support in its
[device list](https://github.com/Sapd/HeadsetControl#supported-headsets), and the driver must report
the headset as offline when it's switched off (most do). Switching then follows within a few seconds
instead of instantly.

## Features

- Switches output **and** microphone, for all Windows roles (default and communications).
- Goes back to whatever you were using before (or to devices you name in the settings).
- Knows the state at startup and after sleep, not just when it changes.
- Tray icon: green = headset on, grey = off, orange = no headset found; tooltip shows battery %.
- HyperX NGENUITY: its virtual "Spatial" devices only work while NGENUITY runs, so the app uses them
  while they're available and falls back to the headset's own device when NGENUITY closes.
- Tiny: one small `.exe`, no installer, no background polling for the Cloud Alpha 2.

## Install

1. Download `HeadsetAutoSwitch.exe` from [Releases](../../releases) and put it in a folder of your
   choice (e.g. `%LOCALAPPDATA%\HeadsetAutoSwitch`).
2. Run it. Right-click the tray icon → **Start with Windows**.
3. For headsets other than the Cloud Alpha 2: put
   [`headsetcontrol.exe`](https://github.com/Sapd/HeadsetControl/releases) next to
   `HeadsetAutoSwitch.exe` (or anywhere on `PATH`).

Requires Windows 10 or 11 (.NET Framework 4.8, built into Windows).

## Settings

Right-click the tray icon → **Edit settings** (`%LOCALAPPDATA%\HeadsetAutoSwitch\config.ini`), then
**Reload settings**. Device names are the ones shown in Windows Sound settings and accept `*` wildcards.

| Setting | Default | Meaning |
| --- | --- | --- |
| `HeadsetOutput` | *(guessed from the headset name)* | Output while the headset is on |
| `HeadsetOutputPreferred` | `NGENUITY - 8 Channel Spatial*` | Preferred output whenever this device is active |
| `SpeakersOutput` | *(previous default)* | Output when the headset turns off |
| `HeadsetMic` | *(guessed from the headset name)* | Microphone while the headset is on |
| `SpeakersMic` | *(previous default)* | Microphone when the headset turns off |
| `Alpha2` | `on` | Built-in Cloud Alpha 2 support |
| `HeadsetControl` | `auto` | `auto`, `off`, or a path to `headsetcontrol.exe` |
| `HeadsetControlInterval` | `5` | Seconds between HeadsetControl checks |
| `HeadsetControlArgs` | | Extra arguments, e.g. `-d 1b1c:0a51` to pick one headset |
| `Notifications` | `false` | Show a notification when the output changes |

If the guessed device names don't match your headset, the log (tray menu → **Open log**) says so;
set `HeadsetOutput` / `HeadsetMic` explicitly.

## Build

No SDK needed: `build.cmd` uses the C# compiler that ships with Windows and produces
`HeadsetAutoSwitch.exe`. GitHub Actions builds every push and attaches the `.exe` to tagged releases.

## How the Cloud Alpha 2 support works

The base station is two USB devices: an audio device (`03f0:0abe`) and a control chip (`03f0:08be`).
The control chip's vendor HID interface (interface 2, usage page `0xff13`) sends `FB 0A 01` when the
headset connects and `FB 0A 00` when it disconnects, and answers `52 01` with `53 01 <connected>`.
The app waits on that interface (no polling) and asks once at startup and after sleep.

Support for this headset in HeadsetControl itself (battery, sidetone, chat-mix, equalizer presets)
has been submitted as [Sapd/HeadsetControl#590](https://github.com/Sapd/HeadsetControl/pull/590).

## AI disclosure

This project was written entirely by an AI assistant, **Claude Opus 5.5** (Claude Code): the code
and this README. The author's role was providing the hardware, running the tests on it and reviewing
the result.

## License

MIT. Not affiliated with HP, HyperX or any headset vendor; product names are used only to describe
compatibility.
