using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HeadsetAutoSwitch;

/// <summary>Device name patterns for one switch, resolved from the settings and the active headset.</summary>
internal sealed record SwitchTargets(
    string HeadsetOutput,
    string HeadsetOutputPreferred,
    string HeadsetMic,
    string SpeakersOutput,
    string SpeakersMic)
{
    public bool IsHeadset(string deviceName) =>
        new[] { HeadsetOutput, HeadsetOutputPreferred, HeadsetMic }.Any(pattern => DeviceName.Matches(deviceName, pattern));
}

/// <param name="Output">The output that is now default, or null if nothing matched.</param>
/// <param name="Complete">False when a device to switch to wasn't available (worth retrying later).</param>
internal sealed record SwitchResult(AudioDevice? Output, bool Complete);

/// <summary>
/// Changes the default devices on one background thread, strictly in the order requested, so a
/// quick off/on is never applied as on/off; COM calls stay off the UI thread. Also remembers which
/// devices are the user's "speakers" to go back to (state.ini).
/// </summary>
internal sealed class AudioSwitcher : IDisposable
{
    // Windows picks a new default by itself when the current one disappears; a default change this
    // soon after an endpoint change is assumed to be that, not the user's choice.
    private static readonly TimeSpan AutomaticChangeWindow = TimeSpan.FromSeconds(3);

    private readonly BlockingCollection<Action> work = new();
    private readonly Thread thread;
    private readonly string?[] lastSetByUs = new string?[2]; // per AudioFlow, worker thread only
    private long endpointsChangedAtTicks;

    public AudioSwitcher()
    {
        thread = new Thread(() =>
        {
            foreach (var job in work.GetConsumingEnumerable())
            {
                try
                {
                    job();
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
                {
                    Log.Write("  audio change failed: " + ex.Message);
                }
            }
        })
        {
            IsBackground = true,
            Name = nameof(AudioSwitcher),
        };
        thread.Start();
    }

    /// <summary>Call from any thread when endpoints are added, removed, enabled or disabled.</summary>
    public void NoteEndpointsChanged() => Interlocked.Exchange(ref endpointsChangedAtTicks, DateTime.UtcNow.Ticks);

    /// <summary>
    /// Switches to the headset (<paramref name="on"/>) or back to the speakers, then calls
    /// <paramref name="done"/> on the worker thread. <paramref name="rememberSpeakers"/> records the
    /// current non-headset defaults first; only pass it when the headset really just turned on.
    /// </summary>
    public void Switch(bool on, SwitchTargets targets, string reason, bool rememberSpeakers, Action<SwitchResult> done) =>
        work.Add(() =>
        {
            Log.Write(reason);
            if (on && rememberSpeakers)
            {
                RememberCurrentDefaults(targets);
            }

            var result = on ? ToHeadset(targets) : ToSpeakers(targets);
            done(result);
        });

    /// <summary>Learns the user's speakers when they pick a non-headset device in Windows.</summary>
    public void NoteDefaultChanged(AudioFlow flow, string deviceId, SwitchTargets targets) =>
        work.Add(() =>
        {
            var automatic = DateTime.UtcNow - new DateTime(Interlocked.Read(ref endpointsChangedAtTicks)) < AutomaticChangeWindow;
            if (deviceId == lastSetByUs[(int)flow] || automatic || Audio.ById(deviceId) is not { } device || targets.IsHeadset(device.Name))
            {
                return;
            }

            if (Remember(flow, device.Name))
            {
                Log.Write($"remembered {(flow == AudioFlow.Output ? "speakers" : "microphone")}: {device.Name}");
            }
        });

    /// <summary>Runs <paramref name="job"/> on the worker thread, after anything already queued.</summary>
    public void Post(Action job) => work.Add(job);

    public void Dispose()
    {
        work.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(2));
        work.Dispose();
    }

    private SwitchResult ToHeadset(SwitchTargets t)
    {
        var output = Set(AudioFlow.Output, t.HeadsetOutputPreferred) ?? Set(AudioFlow.Output, t.HeadsetOutput);
        var mic = Set(AudioFlow.Input, t.HeadsetMic);
        Log.Write($"  output -> {output?.Name ?? "(unchanged)"} | mic -> {mic?.Name ?? "(unchanged)"}");
        if (output is null && t.HeadsetOutput.Length > 0)
        {
            Log.Write($"  no active output matches \"{t.HeadsetOutput}\"; set HeadsetOutput in the settings if this persists");
        }

        var complete = (output is not null || t.HeadsetOutput.Length == 0) && (mic is not null || t.HeadsetMic.Length == 0);
        return new SwitchResult(output, complete);
    }

    private SwitchResult ToSpeakers(SwitchTargets t)
    {
        var state = IniFile.Read(AppPaths.State);
        var outputTarget = t.SpeakersOutput.Length > 0 ? t.SpeakersOutput : state.TryGetValue("Output", out var o) ? o : "";
        var micTarget = t.SpeakersMic.Length > 0 ? t.SpeakersMic : state.TryGetValue("Mic", out var m) ? m : "";
        if (outputTarget.Length == 0)
        {
            Log.Write("  don't know your speakers yet: pick them once in Windows, or set SpeakersOutput in the settings");
        }

        // Remembered names are exact (no '*'), so they match only that device.
        var output = Set(AudioFlow.Output, outputTarget);
        var mic = Set(AudioFlow.Input, micTarget);
        Log.Write($"  output -> {output?.Name ?? "(unchanged)"} | mic -> {mic?.Name ?? "(unchanged)"}");
        var complete = (output is not null || outputTarget.Length == 0) && (mic is not null || micTarget.Length == 0);
        return new SwitchResult(output, complete);
    }

    private AudioDevice? Set(AudioFlow flow, string pattern)
    {
        var device = Audio.SetDefault(flow, pattern);
        if (device is not null)
        {
            lastSetByUs[(int)flow] = device.Value.Id;
        }

        return device;
    }

    private static void RememberCurrentDefaults(SwitchTargets targets)
    {
        foreach (var flow in new[] { AudioFlow.Output, AudioFlow.Input })
        {
            if (Audio.GetDefault(flow) is { } device && !targets.IsHeadset(device.Name))
            {
                Remember(flow, device.Name);
            }
        }
    }

    /// <returns>True if the remembered device changed.</returns>
    private static bool Remember(AudioFlow flow, string deviceName)
    {
        var key = flow == AudioFlow.Output ? "Output" : "Mic";
        var state = IniFile.Read(AppPaths.State);
        if (state.TryGetValue(key, out var current) && current == deviceName)
        {
            return false;
        }

        state[key] = deviceName;
        IniFile.Write(AppPaths.State, state);
        return true;
    }
}
