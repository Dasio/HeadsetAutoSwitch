using System;
using System.Collections.Generic;
using System.IO;

namespace HeadsetAutoSwitch;

/// <summary>Settings from config.ini.</summary>
internal sealed record Config
{
    public const string DefaultPreferredOutput = "NGENUITY - 8 Channel Spatial*";
    public const string DefaultPreferredProcess = "hxn-srv-audio-engine";
    public const string DefaultIgnoreAsSpeakers = "NGENUITY*";

    public string HeadsetOutput { get; init; } = "";
    public string HeadsetOutputPreferred { get; init; } = DefaultPreferredOutput;
    public string HeadsetOutputPreferredProcess { get; init; } = DefaultPreferredProcess;
    public string IgnoreAsSpeakers { get; init; } = DefaultIgnoreAsSpeakers;
    public string SpeakersOutput { get; init; } = "";
    public string HeadsetMic { get; init; } = "";
    public string SpeakersMic { get; init; } = "";
    public bool Alpha2 { get; init; } = true;

    /// <summary>Path to headsetcontrol.exe, "auto" to look for it, or null when off.</summary>
    public string? HeadsetControl { get; init; } = "auto";
    public string HeadsetControlArgs { get; init; } = "";
    public int HeadsetControlInterval { get; init; } = 5;
    public bool Notifications { get; init; }

    private static readonly string[] Template =
    [
        "# HeadsetAutoSwitch settings. Device names accept * wildcards and are matched against the",
        "# names shown in Windows Sound settings. Use \"Reload settings\" in the tray menu after editing.",
        "",
        "# Output while the headset is on. Empty = guess from the headset's name.",
        "HeadsetOutput = ",
        "# Preferred output while the headset is on, used whenever this device is active (e.g. HyperX",
        "# NGENUITY's virtual devices, which only exist while NGENUITY runs). Empty = always HeadsetOutput.",
        $"HeadsetOutputPreferred = {DefaultPreferredOutput}",
        "# ...and only while this process runs (NGENUITY's audio engine: if it crashes, its devices stay",
        "# listed but play nothing). Empty = no check.",
        $"HeadsetOutputPreferredProcess = {DefaultPreferredProcess}",
        "# Output when the headset turns off. Empty = the last device you picked in Windows that isn't",
        "# the headset.",
        "SpeakersOutput = ",
        "",
        "# Microphone while the headset is on. Empty = guess from the headset's name.",
        "HeadsetMic = ",
        "# Microphone when the headset turns off. Empty = the last non-headset microphone you picked.",
        "SpeakersMic = ",
        "# Devices that are never remembered as your speakers or microphone (headset software's own",
        "# virtual devices).",
        $"IgnoreAsSpeakers = {DefaultIgnoreAsSpeakers}",
        "",
        "# Built-in support for the HyperX Cloud Alpha 2 Wireless (instant, no extra software): on/off.",
        "Alpha2 = on",
        "# Other headsets through HeadsetControl: on (looks for headsetcontrol.exe next to this app, in",
        "# its data folder, or on PATH), off, or the full path to headsetcontrol.exe.",
        "HeadsetControl = auto",
        "# Seconds between HeadsetControl checks (2-3600), and extra arguments (e.g. -d to pick a device).",
        "HeadsetControlInterval = 5",
        "HeadsetControlArgs = ",
        "",
        "# Show a notification when the output changes: on/off.",
        "Notifications = off",
    ];

    /// <summary>
    /// Reads <paramref name="path"/>, creating it with commented defaults if missing. Values that
    /// can't be understood fall back to the default and are reported in <paramref name="warnings"/>.
    /// </summary>
    public static Config Load(string path, out List<string> warnings)
    {
        if (!File.Exists(path))
        {
            File.WriteAllLines(path, Template);
        }

        var values = IniFile.Read(path);
        var problems = new List<string>();
        var defaults = new Config();

        string Text(string key, string fallback) => values.TryGetValue(key, out var value) ? value : fallback;

        bool Flag(string key, bool fallback)
        {
            if (!values.TryGetValue(key, out var value) || value.Length == 0)
            {
                return fallback;
            }

            switch (value.ToLowerInvariant())
            {
                case "on" or "true" or "yes" or "1":
                    return true;
                case "off" or "false" or "no" or "0":
                    return false;
                default:
                    problems.Add($"{key} = {value}: expected on or off, using {(fallback ? "on" : "off")}");
                    return fallback;
            }
        }

        string? HeadsetControlSetting()
        {
            var value = Text(nameof(HeadsetControl), "auto");
            switch (value.ToLowerInvariant())
            {
                case "" or "auto" or "on" or "true" or "yes" or "1":
                    return "auto";
                case "off" or "false" or "no" or "0":
                    return null;
            }

            if (File.Exists(value))
            {
                return value;
            }

            problems.Add($"HeadsetControl = {value}: no such file, HeadsetControl support is off");
            return null;
        }

        int Interval()
        {
            var value = Text(nameof(HeadsetControlInterval), "");
            if (value.Length == 0)
            {
                return defaults.HeadsetControlInterval;
            }

            if (int.TryParse(value, out var seconds) && seconds is >= 2 and <= 3600)
            {
                return seconds;
            }

            problems.Add($"HeadsetControlInterval = {value}: expected 2-3600 seconds, using {defaults.HeadsetControlInterval}");
            return defaults.HeadsetControlInterval;
        }

        var config = new Config
        {
            HeadsetOutput = Text(nameof(HeadsetOutput), defaults.HeadsetOutput),
            // "HeadsetOutputWithNgenuity" is what the first, Alpha 2 only version called it.
            HeadsetOutputPreferred = Text(nameof(HeadsetOutputPreferred), Text("HeadsetOutputWithNgenuity", defaults.HeadsetOutputPreferred)),
            HeadsetOutputPreferredProcess = Text(nameof(HeadsetOutputPreferredProcess), defaults.HeadsetOutputPreferredProcess),
            IgnoreAsSpeakers = Text(nameof(IgnoreAsSpeakers), defaults.IgnoreAsSpeakers),
            SpeakersOutput = Text(nameof(SpeakersOutput), defaults.SpeakersOutput),
            HeadsetMic = Text(nameof(HeadsetMic), defaults.HeadsetMic),
            SpeakersMic = Text(nameof(SpeakersMic), defaults.SpeakersMic),
            Alpha2 = Flag(nameof(Alpha2), defaults.Alpha2),
            HeadsetControl = HeadsetControlSetting(),
            HeadsetControlArgs = Text(nameof(HeadsetControlArgs), defaults.HeadsetControlArgs),
            HeadsetControlInterval = Interval(),
            Notifications = Flag(nameof(Notifications), defaults.Notifications),
        };
        warnings = problems;
        return config;
    }
}
