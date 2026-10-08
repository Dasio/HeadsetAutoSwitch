# Changelog

## Unreleased

- Rewritten with current C# (still .NET Framework 4.8, nothing to install); unit tests.
- Switches are applied strictly in order; a headset state read at startup can no longer be missed.
- Learns your speakers from the device you pick in Windows; retries when the device isn't connected yet.
- Checks each Windows role separately, so call apps follow the switch too.
- HeadsetControl: headsets that report "off" without an error (Audeze Maxwell, ASTRO A50 Gen 4) now
  switch back; a failed check no longer leaves the app stuck on "waiting".
- Cloud Alpha 2: battery updates while the headset is on.
- HyperX NGENUITY: its Spatial device is only used while its audio engine runs (a crashed engine
  leaves the device listed but silent); its virtual microphone is never remembered as yours.
- Settings: `on`/`off`/`true`/`false`/`yes`/`no` all work; invalid values are reported in the log.

## 0.1.0 (2026-10-07)

First release.

- HyperX Cloud Alpha 2 Wireless: built-in, instant switching when the headset turns on or off; state read at startup and after sleep.
- Other headsets: experimental support through HeadsetControl.
- Switches output and microphone, returns to the previous devices, tray icon with battery %, start with Windows.
