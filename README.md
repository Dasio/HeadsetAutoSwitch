# HeadsetAutoSwitch

Automatically switches the Windows default audio device to your **wireless headset when you turn it
on**, and back to your **speakers when you turn it off**: output and microphone, no hotkeys.

Works with the **HyperX Cloud Alpha 2 Wireless** out of the box, and with many SteelSeries, Logitech,
Corsair and other wireless headsets through HeadsetControl.

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
- Goes back to your speakers: the devices you last picked in Windows, or ones you name in the
  settings. If something isn't connected at that moment (e.g. a monitor's speakers while the
  display sleeps), it switches as soon as it appears.
- Knows the state at startup and after sleep, not just when it changes.
- Tray icon: green = headset on, grey = off, orange = no headset found; tooltip shows battery %.
- HyperX NGENUITY: its virtual "Spatial" devices only work while NGENUITY runs, so the app uses them
  while they're available and falls back to the headset's own device when NGENUITY closes.
- Tiny: one small `.exe` (about 50 KB), no installer, no background polling for the Cloud Alpha 2.

## Install

1. Download `HeadsetAutoSwitch.exe` from [Releases](../../releases) and put it in a folder of your
   choice (e.g. `%LOCALAPPDATA%\HeadsetAutoSwitch`).
2. Run it. Right-click the tray icon → **Start with Windows**.
3. For headsets other than the Cloud Alpha 2: put
   [`headsetcontrol.exe`](https://github.com/Sapd/HeadsetControl/releases) next to
   `HeadsetAutoSwitch.exe` (or anywhere on `PATH`).

Requires Windows 10 or 11 (.NET Framework 4.8, built into Windows).

**"Windows protected your PC"?** The `.exe` isn't code-signed, so SmartScreen warns about it the
first time you run a downloaded copy. Click **More info → Run anyway**. If you'd rather not trust a
download, build it yourself (see [Build](#build)); it takes a few seconds.

## Settings

Right-click the tray icon → **Edit settings** (`%LOCALAPPDATA%\HeadsetAutoSwitch\config.ini`), then
**Reload settings**. Device names are the ones shown in Windows Sound settings and accept `*` wildcards.

| Setting | Default | Meaning |
| --- | --- | --- |
| `HeadsetOutput` | *(guessed from the headset name)* | Output while the headset is on |
| `HeadsetOutputPreferred` | `NGENUITY - 8 Channel Spatial*` | Preferred output whenever this device is active |
| `SpeakersOutput` | *(last one you picked)* | Output when the headset turns off |
| `HeadsetMic` | *(guessed from the headset name)* | Microphone while the headset is on |
| `SpeakersMic` | *(last one you picked)* | Microphone when the headset turns off |
| `Alpha2` | `on` | Built-in Cloud Alpha 2 support (`on`/`off`) |
| `HeadsetControl` | `auto` | `auto`, `off`, or a path to `headsetcontrol.exe` |
| `HeadsetControlInterval` | `5` | Seconds between HeadsetControl checks (2–3600) |
| `HeadsetControlArgs` | | Extra arguments, e.g. `-d 1b1c:0a51` to pick one headset |
| `Notifications` | `off` | Show a notification when the output changes (`on`/`off`) |

If the guessed device names don't match your headset, or a setting can't be understood, the log
(tray menu → **Open log**) says so.

## Build

With the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer):

```
dotnet test tests/HeadsetAutoSwitch.Tests.csproj
dotnet build src/HeadsetAutoSwitch.csproj -c Release
```

The app targets .NET Framework 4.8, which is part of Windows 10 and 11, so the resulting
`HeadsetAutoSwitch.exe` runs without installing anything. GitHub Actions builds and tests every push
and attaches the `.exe` to tagged releases.

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

## Common questions

**Windows doesn't switch to my speakers when I turn off my wireless headset. Why?**
The USB dongle or base station stays plugged in, so for Windows the headset never disappears. This
app asks the headset itself whether it's on, and switches for you.

**My headset still shows up in the sound devices list even when it's off.**
Same reason: the device in the list is the dongle, not the headset. HeadsetAutoSwitch makes that
harmless by moving the default device away from it while the headset is off.

**Is this an alternative to SoundSwitch or a hotkey to change audio output?**
It complements them: hotkey tools switch when you press a key, this switches by itself when the
headset powers on or off. You can keep a hotkey tool for manual switching.

**Does it need HyperX NGENUITY?**
No. For the Cloud Alpha 2 it talks to the base station directly. It also works alongside NGENUITY
and follows its "Spatial" devices when NGENUITY is running.

## Maintenance

Maintained on a best-effort basis in spare time. Bug reports with the log attached are the most
helpful; pull requests are welcome (see [CONTRIBUTING.md](CONTRIBUTING.md)).

## License

MIT. Not affiliated with HP, HyperX or any headset vendor; product names are used only to describe
compatibility.
