using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;

namespace HeadsetAutoSwitch;

/// <summary>
/// Any headset that <see href="https://github.com/Sapd/HeadsetControl">HeadsetControl</see> can read
/// the battery of. Most wireless headsets only answer when asked, so this runs
/// <c>headsetcontrol -b -o json</c> every few seconds.
/// </summary>
internal sealed class HeadsetControlMonitor : IHeadsetMonitor
{
    private const int TimeoutMs = 15_000;

    private readonly string exe;
    private readonly string extraArgs;
    private readonly int intervalMs;
    private readonly Func<string, string, bool> skip;
    private readonly AutoResetEvent wake = new(false);
    private volatile bool stopping;
    private volatile HeadsetReading? last;
    private volatile int onState = -1; // -1 unknown, 0 off, 1 on

    /// <param name="exe">Path to headsetcontrol.exe.</param>
    /// <param name="extraArgs">Extra arguments, e.g. <c>-d 1b1c:0a51</c> to pick one headset.</param>
    /// <param name="intervalSeconds">Seconds between checks.</param>
    /// <param name="skip">Headsets (vendor id, product id) handled by another monitor.</param>
    public HeadsetControlMonitor(string exe, string extraArgs, int intervalSeconds, Func<string, string, bool> skip)
    {
        this.exe = exe;
        this.extraArgs = extraArgs;
        intervalMs = Math.Min(Math.Max(intervalSeconds, 2), 3600) * 1000;
        this.skip = skip;
    }

    public event Action<bool, bool>? ConnectionChanged;
    public event Action<int>? BatteryChanged;
    public event Action<bool>? PresenceChanged;

    public string Name => last?.Name ?? "Headset";
    public bool? IsOn => onState < 0 ? null : onState == 1;
    public string DefaultOutput => last is { Product.Length: > 0 } reading ? $"*{reading.Product}*" : "";
    public string DefaultMic => DefaultOutput;

    /// <summary>headsetcontrol.exe next to the app, in its data folder, or on PATH; null if none.</summary>
    public static string? Find(string dataDir)
    {
        var folders = new[] { AppDomain.CurrentDomain.BaseDirectory, dataDir }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            .Select(folder => folder.Trim().Trim('"'))
            .Where(folder => folder.Length > 0);
        // Relative PATH entries (".") would depend on the working directory; never run those.
        foreach (var folder in folders)
        {
            try
            {
                var candidate = Path.Combine(folder, "headsetcontrol.exe");
                if (Path.IsPathRooted(folder) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; ignore it.
            }
        }

        return null;
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = nameof(HeadsetControlMonitor) }.Start();

    // The thread disposes the wake event when it exits, so it is never disposed under a WaitOne.
    public void Dispose()
    {
        stopping = true;
        Wake();
    }

    public void Refresh() => Wake();

    private void Wake()
    {
        try
        {
            wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // The thread has already finished.
        }
    }

    private void Run()
    {
        Log.Write("HeadsetControl: using " + exe);
        bool? present = null, on = null;
        var offReadings = 0;
        while (!stopping)
        {
            // A failed run (timeout, crash, unreadable output) tells nothing: keep the current state.
            if (TryPoll(out var reading))
            {
                var found = reading is not null;
                if (present != found)
                {
                    present = found;
                    // After the headset goes missing, report the state again when it's back.
                    on = null;
                    onState = -1;
                    offReadings = 0;
                    Log.Write(found ? $"HeadsetControl: found {reading!.Name}" : "HeadsetControl: no supported headset, waiting");
                    PresenceChanged?.Invoke(found);
                }

                if (reading is { On: { } isOn })
                {
                    last = reading;
                    // A single "off" can be a hiccup while the headset reconnects: wait for a second one.
                    offReadings = isOn ? 0 : offReadings + 1;
                    if ((isOn || offReadings >= 2 || on is null) && on != isOn)
                    {
                        var initial = on is null;
                        on = isOn;
                        onState = isOn ? 1 : 0;
                        ConnectionChanged?.Invoke(isOn, initial);
                    }

                    if (reading.Battery >= 0)
                    {
                        BatteryChanged?.Invoke(reading.Battery);
                    }
                }
                else if (reading is not null)
                {
                    last = reading;
                }
            }

            wake.WaitOne(intervalMs);
        }

        wake.Dispose();
    }

    /// <returns>False when HeadsetControl couldn't be asked; otherwise the reading (null = no headset).</returns>
    private bool TryPoll(out HeadsetReading? reading)
    {
        reading = null;
        var json = RunHeadsetControl();
        if (json is null)
        {
            return false;
        }

        try
        {
            reading = HeadsetControlOutput.Parse(json, skip);
            return true;
        }
        catch (SerializationException ex)
        {
            Log.Write("HeadsetControl: unreadable output: " + ex.Message);
            return false;
        }
    }

    private string? RunHeadsetControl()
    {
        var startInfo = new ProcessStartInfo(exe, $"-b -o json {extraArgs}".Trim())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo)!;
            // Read both pipes concurrently so a full stderr pipe can't block the process.
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeoutMs))
            {
                process.Kill();
                Log.Write($"HeadsetControl: no answer within {TimeoutMs / 1000} s");
                return null;
            }

            return output.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write("HeadsetControl: cannot run: " + ex.Message);
            return null;
        }
    }
}
