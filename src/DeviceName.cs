using System;
using System.Text.RegularExpressions;

namespace HeadsetAutoSwitch;

/// <summary>Matches Windows audio endpoint names against user patterns with <c>*</c> wildcards.</summary>
internal static class DeviceName
{
    /// <summary>
    /// True when <paramref name="name"/> matches <paramref name="pattern"/>, case-insensitively.
    /// <c>*</c> matches any text; everything else is literal (brackets in names like
    /// "Speakers (Realtek(R) Audio)" need no escaping). An empty pattern matches nothing.
    /// </summary>
    public static bool Matches(string? name, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || name is null)
        {
            return false;
        }

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
