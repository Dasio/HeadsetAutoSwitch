using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HeadsetAutoSwitch;

/// <summary>
/// The tray icon and the switching decisions. All state lives on the UI thread: monitor and
/// endpoint events are marshalled here first, and device changes go to <see cref="AudioSwitcher"/>.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "HeadsetAutoSwitch";

    // NGENUITY enables its virtual devices in a burst of changes ~0.5 s after its audio engine
    // starts; deciding on the first change picks the wrong device.
    private const int EndpointSettleMs = 1500;

    // Wireless links drop during sleep and come back about a second after waking, often several
    // seconds before Windows reports the resume. From suspend until things settle, on/off reports are
    // held and only an end state that differs from before sleep is acted on.
    private const int ResumeSettleMs = 15_000;
    private const int MissingResumeFallbackMs = 30_000; // in case Windows never reports the resume

    private static readonly Color OnColor = Color.FromArgb(60, 180, 90);
    private static readonly Color OffColor = Color.FromArgb(140, 140, 140);
    private static readonly Color MissingColor = Color.FromArgb(200, 120, 40);

    private readonly NotifyIcon tray = new();
    private readonly Control ui = new();
    private readonly ToolStripMenuItem statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem autostartItem = new("Start with Windows");
    private readonly AudioSwitcher switcher = new();
    private readonly List<IHeadsetMonitor> monitors = []; // in order of preference
    private readonly HashSet<IHeadsetMonitor> present = [];
    private readonly System.Threading.Timer settleTimer;
    private readonly System.Threading.Timer resumeTimer;
    private readonly EndpointWatcher endpointWatcher;
    private readonly ProcessExitWatcher preferredProcessWatcher;

    private Config config;
    private IHeadsetMonitor? active;   // the present monitor that drives switching
    private bool? headsetOn;           // what the active monitor last reported; null = not yet
    private bool? routedToHeadset;     // where we last sent the audio (manual switches included)
    private bool? headsetOnBeforeSleep;
    private bool sleeping;             // from suspend until ResumeSettleMs after waking up
    private DateTime sleepSettlesAt;   // when the hold may end; a timer firing earlier is stale
    private bool retryPending;         // last switch couldn't reach a device; retry when devices change
    private int battery = -1;
    private string? currentOutput;
    private Color iconColor;
    private IntPtr iconHandle;
    private bool preferredAvailable;   // owned by the switcher thread

    public TrayApp()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        MigrateFromAlpha2AutoSwitch();
        config = LoadConfig();
        ui.CreateControl(); // gives BeginInvoke a window on this thread

        tray.ContextMenuStrip = BuildMenu();
        tray.Visible = true;

        CreateMonitors();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        settleTimer = new System.Threading.Timer(_ => OnUi(OnEndpointsSettled));
        resumeTimer = new System.Threading.Timer(_ => OnUi(OnResumeSettled));
        preferredProcessWatcher = new ProcessExitWatcher(() => OnUi(() =>
        {
            Log.Write($"{config.HeadsetOutputPreferredProcess} exited");
            UpdatePreferredAvailability();
        }));
        UpdatePreferredAvailability();
        endpointWatcher = new EndpointWatcher(
            endpointsChanged: () =>
            {
                switcher.NoteEndpointsChanged();
                settleTimer.Change(EndpointSettleMs, System.Threading.Timeout.Infinite);
            },
            defaultChanged: (flow, id) =>
            {
                var changedAt = DateTime.UtcNow;
                OnUi(() => switcher.NoteDefaultChanged(flow, id, changedAt, Targets()));
            });

        UpdateTray();
        foreach (var monitor in monitors)
        {
            monitor.Start();
        }
    }

    private static Config LoadConfig()
    {
        var config = Config.Load(AppPaths.Config, out var warnings);
        foreach (var warning in warnings)
        {
            Log.Write("settings: " + warning);
        }

        return config;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Switch to headset now", null, (_, _) => Switch(on: true, "manual: headset", rememberSpeakers: active is not null));
        menu.Items.Add("Switch to speakers now", null, (_, _) => Switch(on: false, "manual: speakers", rememberSpeakers: false));
        menu.Items.Add(new ToolStripSeparator());
        autostartItem.Click += (_, _) => SetAutostart(!autostartItem.Checked);
        menu.Items.Add(autostartItem);
        menu.Items.Add("Edit settings", null, (_, _) => OpenInNotepad(AppPaths.Config));
        menu.Items.Add("Reload settings", null, (_, _) => ReloadSettings());
        menu.Items.Add("Open log", null, (_, _) => OpenInNotepad(AppPaths.Log));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => autostartItem.Checked = IsAutostart();
        return menu;
    }

    private void CreateMonitors()
    {
        if (config.Alpha2)
        {
            monitors.Add(new Alpha2Monitor());
        }

        var headsetControl = config.HeadsetControl == "auto" ? HeadsetControlMonitor.Find(AppPaths.DataDir) : config.HeadsetControl;
        if (headsetControl is not null)
        {
            // With the built-in Cloud Alpha 2 support on, leave that headset to it.
            var alpha2Builtin = config.Alpha2;
            monitors.Add(new HeadsetControlMonitor(headsetControl, config.HeadsetControlArgs, config.HeadsetControlInterval,
                (vendor, product) => alpha2Builtin && vendor == "0x03f0" && product == "0x08be"));
        }

        foreach (var monitor in monitors)
        {
            monitor.PresenceChanged += isPresent => OnUi(() => OnPresenceChanged(monitor, isPresent));
            monitor.ConnectionChanged += (on, initial) => OnUi(() => OnConnectionChanged(monitor, on, initial));
            monitor.BatteryChanged += percent => OnUi(() => OnBatteryChanged(monitor, percent));
        }
    }

    private void OnPresenceChanged(IHeadsetMonitor monitor, bool isPresent)
    {
        if (isPresent)
        {
            present.Add(monitor);
        }
        else
        {
            present.Remove(monitor);
        }

        var next = monitors.FirstOrDefault(present.Contains);
        if (next != active)
        {
            active = next;
            headsetOn = null;
            battery = -1;
            Log.Write(next is null ? "no headset found" : "following " + next.Name);
            // Its state may have been reported while another monitor was active.
            if (next?.IsOn is { } on)
            {
                OnConnectionChanged(next, on, initial: true);
            }
        }

        UpdateTray();
    }

    private void OnConnectionChanged(IHeadsetMonitor monitor, bool on, bool initial)
    {
        // A state re-read (startup, resume, reconnect) that matches what we know is not news; in
        // particular it must not undo a manual switch.
        if (monitor != active || (initial && headsetOn == on))
        {
            return;
        }

        headsetOn = on;
        if (sleeping)
        {
            Log.Write($"{monitor.Name} {(on ? "ON" : "OFF")} (around sleep, waiting for it to settle)");
            if (sleepSettlesAt == DateTime.MaxValue)
            {
                // Still waiting for Windows to report the resume; don't hold forever if it never does.
                StartSleepSettle(MissingResumeFallbackMs);
            }

            UpdateTray();
            return;
        }

        Switch(on, $"{monitor.Name} {(on ? "ON" : "OFF")}{(initial ? " (state at connect)" : "")}", rememberSpeakers: on);
    }

    private void StartSleepSettle(int delayMs)
    {
        sleepSettlesAt = DateTime.UtcNow.AddMilliseconds(delayMs);
        resumeTimer.Change(delayMs, System.Threading.Timeout.Infinite);
    }

    private void OnResumeSettled()
    {
        // A timer started for an earlier sleep can fire right after the next wake-up: ignore it.
        if (!sleeping || DateTime.UtcNow < sleepSettlesAt.AddMilliseconds(-250))
        {
            return;
        }

        sleeping = false;
        // Same state as before sleep (the usual link drop and reconnect): leave the audio where it
        // was, including a manual choice.
        if (headsetOn is { } on && on != headsetOnBeforeSleep)
        {
            Switch(on, $"{active?.Name} {(on ? "ON" : "OFF")} (changed during sleep)", rememberSpeakers: on);
        }
    }

    private void OnBatteryChanged(IHeadsetMonitor monitor, int percent)
    {
        if (monitor == active && percent != battery)
        {
            battery = percent;
            UpdateTray();
        }
    }

    private void OnEndpointsSettled()
    {
        // A device the last switch needed may have appeared (e.g. a monitor's speakers waking up).
        if (retryPending && routedToHeadset is { } on)
        {
            Switch(on, "retrying: audio devices changed", rememberSpeakers: false);
        }

        UpdatePreferredAvailability();
    }

    // Called on the switcher thread: the preferred output exists and, if configured, its process runs.
    private Func<bool> PreferredUsable()
    {
        var pattern = config.HeadsetOutputPreferred;
        var process = config.HeadsetOutputPreferredProcess;
        return () => Audio.IsActive(AudioFlow.Output, pattern) && (process.Length == 0 || preferredProcessWatcher.IsRunning(process));
    }

    // The preferred output (e.g. NGENUITY's virtual device) appeared or disappeared while audio goes
    // to the headset: move to it, or off it before applications are left playing into a dead endpoint.
    private void UpdatePreferredAvailability()
    {
        var pattern = config.HeadsetOutputPreferred;
        var usable = PreferredUsable();
        switcher.Post(() =>
        {
            var available = usable();
            if (available == preferredAvailable)
            {
                return;
            }

            preferredAvailable = available;
            OnUi(() =>
            {
                if (routedToHeadset == true)
                {
                    Switch(on: true, $"{pattern} {(available ? "became active" : "went away")}", rememberSpeakers: false);
                }
            });
        });
    }

    private void Switch(bool on, string reason, bool rememberSpeakers)
    {
        routedToHeadset = on;
        retryPending = false;
        switcher.Switch(on, Targets(), reason, rememberSpeakers, PreferredUsable(), result => OnUi(() =>
        {
            // A newer switch may have been requested meanwhile; its result decides.
            if (routedToHeadset != on)
            {
                return;
            }

            retryPending = !result.Complete;
            if (result.Output is { } output && output.Name != currentOutput)
            {
                currentOutput = output.Name;
                if (config.Notifications)
                {
                    tray.ShowBalloonTip(2000, "Audio output", output.Name, ToolTipIcon.None);
                }
            }

            UpdateTray();
        }));
    }

    private SwitchTargets Targets() => new(
        HeadsetOutput: config.HeadsetOutput.Length > 0 ? config.HeadsetOutput : active?.DefaultOutput ?? "",
        HeadsetOutputPreferred: config.HeadsetOutputPreferred,
        HeadsetMic: config.HeadsetMic.Length > 0 ? config.HeadsetMic : active?.DefaultMic ?? "",
        SpeakersOutput: config.SpeakersOutput,
        SpeakersMic: config.SpeakersMic,
        IgnoreAsSpeakers: config.IgnoreAsSpeakers);

    private void ReloadSettings()
    {
        config = LoadConfig();
        Log.Write("settings reloaded (changes to headset sources apply after a restart)");
        UpdatePreferredAvailability();
    }

    // Raised on SystemEvents' own thread.
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => OnUi(() =>
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                // Asleep again before the last wake-up settled: keep the state from before that sleep.
                if (!sleeping)
                {
                    headsetOnBeforeSleep = headsetOn;
                }

                sleeping = true;
                sleepSettlesAt = DateTime.MaxValue;
                resumeTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                Log.Write("going to sleep");
                break;
            case PowerModes.Resume:
                Log.Write("resumed from sleep");
                if (!sleeping)
                {
                    headsetOnBeforeSleep = headsetOn; // a resume without a suspend notification
                }

                sleeping = true;
                StartSleepSettle(ResumeSettleMs);
                foreach (var monitor in monitors)
                {
                    monitor.Refresh();
                }

                break;
        }
    });

    private void OnUi(Action action)
    {
        if (ui.IsHandleCreated && !ui.IsDisposed)
        {
            ui.BeginInvoke(action);
        }
    }

    private void UpdateTray()
    {
        var state = monitors.Count == 0 ? "no headset sources enabled"
            : active is null ? "no headset found"
            : headsetOn switch { true => "on", false => "off", null => "waiting" };
        var status = $"{active?.Name ?? "HeadsetAutoSwitch"}: {state}";
        if (battery >= 0 && headsetOn == true)
        {
            status += $" · {battery}%";
        }

        statusItem.Text = currentOutput is null ? status : $"{status}\nOutput: {currentOutput}";
        tray.Text = status.Length > 63 ? status.Substring(0, 63) : status; // Windows' tooltip limit
        SetIcon(active is null ? MissingColor : headsetOn == true ? OnColor : OffColor);
    }

    // A small headphone glyph, tinted by state.
    private void SetIcon(Color color)
    {
        if (color == iconColor && iconHandle != IntPtr.Zero)
        {
            return;
        }

        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var pen = new Pen(color, Math.Max(2f, size / 8f)))
        using (var brush = new SolidBrush(color))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float margin = size * 0.12f, arc = size - 2 * margin;
            float cupWidth = size * 0.26f, cupHeight = size * 0.42f, cupTop = size * 0.5f;
            graphics.DrawArc(pen, margin, margin, arc, arc * 1.1f, 180, 180);
            graphics.FillRectangle(brush, margin - pen.Width / 2, cupTop, cupWidth, cupHeight);
            graphics.FillRectangle(brush, size - margin + pen.Width / 2 - cupWidth, cupTop, cupWidth, cupHeight);
        }

        var handle = bitmap.GetHicon();
        tray.Icon = Icon.FromHandle(handle);
        if (iconHandle != IntPtr.Zero)
        {
            DestroyIcon(iconHandle);
        }

        iconHandle = handle;
        iconColor = color;
    }

    private static void OpenInNotepad(string path)
    {
        using var process = Process.Start("notepad.exe", $"\"{path}\"");
    }

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    private static void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
        {
            key.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    // The first version was called Alpha2AutoSwitch: carry its settings and autostart over once.
    private static void MigrateFromAlpha2AutoSwitch()
    {
        var oldDir = Path.Combine(Path.GetDirectoryName(AppPaths.DataDir)!, "Alpha2AutoSwitch");
        foreach (var (oldFile, newFile) in new[] { ("config.ini", AppPaths.Config), ("state.ini", AppPaths.State) })
        {
            var source = Path.Combine(oldDir, oldFile);
            if (File.Exists(source) && !File.Exists(newFile))
            {
                File.Copy(source, newFile);
                Log.Write($"copied {oldFile} from Alpha2AutoSwitch");
            }
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue("Alpha2AutoSwitch") is not null)
        {
            key.DeleteValue("Alpha2AutoSwitch");
            key.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
            Log.Write("moved start with Windows over from Alpha2AutoSwitch");
        }
    }

    protected override void ExitThreadCore()
    {
        Log.Write("exiting");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        endpointWatcher.Dispose();
        settleTimer.Dispose();
        resumeTimer.Dispose();
        preferredProcessWatcher.Dispose();
        foreach (var monitor in monitors)
        {
            monitor.Dispose();
        }

        switcher.Dispose();
        tray.Visible = false;
        tray.Dispose();
        if (iconHandle != IntPtr.Zero)
        {
            DestroyIcon(iconHandle);
        }

        base.ExitThreadCore();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
