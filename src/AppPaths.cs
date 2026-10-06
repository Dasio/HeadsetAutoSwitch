using System;
using System.IO;

namespace HeadsetAutoSwitch;

/// <summary>Where the app keeps its files: %LOCALAPPDATA%\HeadsetAutoSwitch.</summary>
internal static class AppPaths
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeadsetAutoSwitch");

    public static string Config => Path.Combine(DataDir, "config.ini");

    /// <summary>Devices that were default before the headset took over.</summary>
    public static string State => Path.Combine(DataDir, "state.ini");

    public static string Log => Path.Combine(DataDir, "log.txt");
}
