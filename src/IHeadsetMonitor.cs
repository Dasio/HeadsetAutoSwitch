using System;

namespace HeadsetAutoSwitch;

/// <summary>
/// A source that knows whether a headset is on. Events are raised on the monitor's own background
/// thread. <see cref="IDisposable.Dispose"/> stops it.
/// </summary>
internal interface IHeadsetMonitor : IDisposable
{
    /// <summary>The headset turned on/off. <c>initial</c> is true for the state read at (re)connect.</summary>
    event Action<bool /*on*/, bool /*initial*/>? ConnectionChanged;

    /// <summary>Battery level in percent.</summary>
    event Action<int>? BatteryChanged;

    /// <summary>The dongle / base station appeared or disappeared.</summary>
    event Action<bool>? PresenceChanged;

    string Name { get; }

    /// <summary>Output device name pattern used when the HeadsetOutput setting is empty.</summary>
    string DefaultOutput { get; }

    /// <summary>Microphone name pattern used when the HeadsetMic setting is empty.</summary>
    string DefaultMic { get; }

    void Start();

    /// <summary>Re-read the state, e.g. after resume from sleep.</summary>
    void Refresh();
}
