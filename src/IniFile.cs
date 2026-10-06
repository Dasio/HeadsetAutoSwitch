using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HeadsetAutoSwitch;

/// <summary>Minimal <c>key = value</c> files with <c>#</c> comments (config.ini, state.ini).</summary>
internal static class IniFile
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            var separator = line.IndexOf('=');
            if (separator > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            {
                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }
        }

        return values;
    }

    public static void Write(string path, IDictionary<string, string> values) =>
        File.WriteAllLines(path, values.Select(kv => $"{kv.Key} = {kv.Value}"));
}
