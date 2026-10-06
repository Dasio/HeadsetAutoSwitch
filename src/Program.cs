// HeadsetAutoSwitch - switches the Windows default audio devices when a wireless headset is powered
// on or off, even though its dongle / base station stays plugged in.
//
// Build (no SDK needed, csc ships with Windows): see build.cmd
// Not affiliated with HP, HyperX or any headset vendor.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("HeadsetAutoSwitch")]
[assembly: System.Reflection.AssemblyProduct("HeadsetAutoSwitch")]
[assembly: System.Reflection.AssemblyVersion("0.1.0.0")]

namespace HeadsetAutoSwitch
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\HeadsetAutoSwitch", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.Run(new TrayApp());
            }
        }
    }

    // ------------------------------------------------------------------ config / log

    class Config
    {
        public string HeadsetOutput, HeadsetOutputPreferred, SpeakersOutput, HeadsetMic, SpeakersMic;
        public string HeadsetControl, HeadsetControlArgs;
        public int HeadsetControlInterval;
        public bool Alpha2, Notifications;

        static readonly string[] Template = {
            "# HeadsetAutoSwitch settings. Device names accept * wildcards and are matched against the",
            "# names shown in Windows Sound settings. Use \"Reload settings\" in the tray menu after editing.",
            "",
            "# Output while the headset is on. Empty = guess from the headset's name.",
            "HeadsetOutput = ",
            "# Preferred output while the headset is on, used whenever this device is active (e.g. HyperX",
            "# NGENUITY's virtual devices, which only exist while NGENUITY runs). Empty = always HeadsetOutput.",
            "HeadsetOutputPreferred = NGENUITY - 8 Channel Spatial*",
            "# Output when the headset turns off. Empty = whatever was default before the headset took over.",
            "SpeakersOutput = ",
            "",
            "# Microphone while the headset is on. Empty = guess from the headset's name.",
            "HeadsetMic = ",
            "# Microphone when the headset turns off. Empty = whatever was default before.",
            "SpeakersMic = ",
            "",
            "# Built-in support for the HyperX Cloud Alpha 2 Wireless (instant, no extra software).",
            "Alpha2 = on",
            "# Other headsets through HeadsetControl: auto (headsetcontrol.exe next to this app, in its",
            "# data folder, or on PATH), off, or a full path to headsetcontrol.exe.",
            "HeadsetControl = auto",
            "# Seconds between HeadsetControl checks, and extra arguments (e.g. -d to pick a device).",
            "HeadsetControlInterval = 5",
            "HeadsetControlArgs = ",
            "",
            "Notifications = false",
        };

        public static Config Load(string path)
        {
            if (!File.Exists(path)) File.WriteAllLines(path, Template);
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            Func<string, string, string> get = (k, def) => { string v; return kv.TryGetValue(k, out v) ? v : def; };
            int interval;
            if (!int.TryParse(get("HeadsetControlInterval", "5"), out interval)) interval = 5;
            return new Config
            {
                HeadsetOutput = get("HeadsetOutput", ""),
                // HeadsetOutputWithNgenuity is the name used by the first (Alpha 2 only) version
                HeadsetOutputPreferred = get("HeadsetOutputPreferred", get("HeadsetOutputWithNgenuity", "")),
                SpeakersOutput = get("SpeakersOutput", ""),
                HeadsetMic = get("HeadsetMic", ""),
                SpeakersMic = get("SpeakersMic", ""),
                Alpha2 = !get("Alpha2", "on").Equals("off", StringComparison.OrdinalIgnoreCase),
                HeadsetControl = get("HeadsetControl", "auto"),
                HeadsetControlArgs = get("HeadsetControlArgs", ""),
                HeadsetControlInterval = interval,
                Notifications = get("Notifications", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            };
        }
    }

    static class Log
    {
        static readonly object Sync = new object();
        public static string FilePath;

        public static void Write(string msg)
        {
            lock (Sync)
            {
                try
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        File.Copy(FilePath, FilePath + ".old", true);
                        File.Delete(FilePath);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------ tray app

    class TrayApp : ApplicationContext
    {
        static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeadsetAutoSwitch");
        static readonly string ConfigPath = Path.Combine(DataDir, "config.ini");
        static readonly string StatePath = Path.Combine(DataDir, "state.ini");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "HeadsetAutoSwitch";

        readonly NotifyIcon tray = new NotifyIcon();
        readonly Control ui = new Control();
        readonly List<IHeadsetMonitor> monitors = new List<IHeadsetMonitor>();   // in order of preference
        readonly HashSet<IHeadsetMonitor> present = new HashSet<IHeadsetMonitor>();
        readonly System.Threading.Timer settle;
        EndpointWatcher watcher;
        readonly object switchLock = new object();
        readonly ToolStripMenuItem statusItem = new ToolStripMenuItem { Enabled = false };
        readonly ToolStripMenuItem autostartItem = new ToolStripMenuItem("Start with Windows");

        Config cfg;
        volatile IHeadsetMonitor active;   // the present monitor that drives switching
        bool? headsetOn;                   // null until the active monitor reports
        int battery = -1;
        bool preferredActive;
        string currentOutput;
        IntPtr iconHandle;

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public TrayApp()
        {
            MigrateFromAlpha2AutoSwitch();
            Directory.CreateDirectory(DataDir);
            Log.FilePath = Path.Combine(DataDir, "log.txt");
            cfg = Config.Load(ConfigPath);
            ui.CreateControl();
            Log.Write("started");

            var menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Switch to headset now", null, delegate { Async(delegate { ApplyHeadset(true, "manual"); }); });
            menu.Items.Add("Switch to speakers now", null, delegate { Async(delegate { ApplyHeadset(false, "manual"); }); });
            menu.Items.Add(new ToolStripSeparator());
            autostartItem.Click += delegate { SetAutostart(!autostartItem.Checked); };
            menu.Items.Add(autostartItem);
            menu.Items.Add("Edit settings", null, delegate { Process.Start("notepad.exe", "\"" + ConfigPath + "\""); });
            menu.Items.Add("Reload settings", null, delegate { cfg = Config.Load(ConfigPath); Log.Write("settings reloaded (headset sources apply after restart)"); });
            menu.Items.Add("Open log", null, delegate { Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });
            menu.Opening += delegate { autostartItem.Checked = IsAutostart(); };
            tray.ContextMenuStrip = menu;
            tray.Visible = true;

            if (cfg.Alpha2) monitors.Add(new Alpha2Monitor());
            string hc = cfg.HeadsetControl.Equals("auto", StringComparison.OrdinalIgnoreCase) ? HeadsetControlMonitor.Find(DataDir)
                : cfg.HeadsetControl.Equals("off", StringComparison.OrdinalIgnoreCase) ? null
                : cfg.HeadsetControl;
            if (hc != null)
            {
                bool alpha2Native = cfg.Alpha2;
                // With the built-in Alpha 2 support on, leave that headset to it.
                monitors.Add(new HeadsetControlMonitor(hc, cfg.HeadsetControlArgs, cfg.HeadsetControlInterval,
                    (vid, pid) => alpha2Native && vid == "0x03f0" && pid == "0x08be"));
            }
            foreach (var m in monitors) Wire(m);

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            preferredActive = Audio.IsActive(Audio.Render, cfg.HeadsetOutputPreferred);
            settle = new System.Threading.Timer(delegate { EndpointsSettled(); });
            // Endpoint changes come in bursts (NGENUITY enables four devices in ~400 ms); act once they settle.
            watcher = new EndpointWatcher(delegate { settle.Change(1500, Timeout.Infinite); });

            Refresh();
            foreach (var m in monitors) m.Start();
        }

        void Wire(IHeadsetMonitor m)
        {
            m.PresenceChanged += isPresent => Ui(delegate
            {
                if (isPresent) present.Add(m); else present.Remove(m);
                IHeadsetMonitor next = null;
                foreach (var candidate in monitors) if (present.Contains(candidate)) { next = candidate; break; }
                if (next != active)
                {
                    active = next;
                    headsetOn = null;
                    battery = -1;
                    if (next != null) Log.Write("following " + next.Name);
                }
                Refresh();
            });
            m.ConnectionChanged += (on, initial) => Async(delegate
            {
                if (m != active) return;
                // A startup reading that matches what we already know is not news.
                if (initial && headsetOn == on) return;
                ApplyHeadset(on, m.Name + " " + (on ? "ON" : "OFF") + (initial ? " (at start)" : ""));
            });
            m.BatteryChanged += pct => Ui(delegate { if (m == active) { battery = pct; Refresh(); } });
        }

        void Ui(Action a) { if (ui.IsHandleCreated) ui.BeginInvoke(a); }
        static void Async(Action a) { ThreadPool.QueueUserWorkItem(delegate { a(); }); }

        void OnPowerModeChanged(object s, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            Log.Write("resumed from sleep");
            foreach (var m in monitors) m.Reopen();
        }

        string OutputPattern(IHeadsetMonitor m) { return cfg.HeadsetOutput.Length > 0 ? cfg.HeadsetOutput : (m == null ? "" : m.DefaultOutput); }
        string MicPattern(IHeadsetMonitor m) { return cfg.HeadsetMic.Length > 0 ? cfg.HeadsetMic : (m == null ? "" : m.DefaultMic); }

        // The preferred headset output (e.g. NGENUITY's virtual device) appeared or disappeared while
        // the headset is on: move to it, or off it before apps are left playing into a dead endpoint.
        void EndpointsSettled()
        {
            bool isActive = Audio.IsActive(Audio.Render, cfg.HeadsetOutputPreferred);
            if (isActive == preferredActive) return;
            preferredActive = isActive;
            if (headsetOn == true)
                ApplyHeadset(true, cfg.HeadsetOutputPreferred + (isActive ? " became active" : " went away"));
        }

        void ApplyHeadset(bool on, string reason)
        {
            lock (switchLock)
            {
                Log.Write(reason);
                var m = active;
                try
                {
                    string output, mic;
                    if (on)
                    {
                        RememberSpeakers(m);
                        output = Audio.SetDefaultByPattern(Audio.Render, cfg.HeadsetOutputPreferred);
                        if (output == null) output = Audio.SetDefaultByPattern(Audio.Render, OutputPattern(m));
                        mic = Audio.SetDefaultByPattern(Audio.Capture, MicPattern(m));
                        if (output == null && OutputPattern(m).Length > 0)
                            Log.Write("  no active output matches \"" + OutputPattern(m) + "\" - set HeadsetOutput in the settings");
                    }
                    else
                    {
                        output = Audio.SetDefaultByPattern(Audio.Render, Pick(cfg.SpeakersOutput, "Output"));
                        mic = Audio.SetDefaultByPattern(Audio.Capture, Pick(cfg.SpeakersMic, "Mic"));
                    }
                    Log.Write("  output -> " + (output ?? "(unchanged)") + " | mic -> " + (mic ?? "(unchanged)"));
                    Ui(delegate
                    {
                        headsetOn = on;
                        if (output != null) currentOutput = output;
                        Refresh();
                        if (cfg.Notifications && output != null) tray.ShowBalloonTip(2000, "Audio output", output, ToolTipIcon.None);
                    });
                }
                catch (Exception ex) { Log.Write("  switch failed: " + ex.Message); }
            }
        }

        // Before the headset takes over, remember which non-headset devices were default so we can
        // go back to them (used when SpeakersOutput / SpeakersMic are left empty).
        void RememberSpeakers(IHeadsetMonitor m)
        {
            var state = ReadState();
            string o = Audio.DefaultName(Audio.Render), mic = Audio.DefaultName(Audio.Capture);
            if (o != null && !IsHeadsetDevice(o, m)) state["Output"] = o;
            if (mic != null && !IsHeadsetDevice(mic, m)) state["Mic"] = mic;
            try
            {
                var lines = new List<string>();
                foreach (var kv in state) lines.Add(kv.Key + " = " + kv.Value);
                File.WriteAllLines(StatePath, lines.ToArray());
            }
            catch { }
        }

        bool IsHeadsetDevice(string name, IHeadsetMonitor m)
        {
            return Audio.Matches(name, OutputPattern(m)) || Audio.Matches(name, MicPattern(m))
                || Audio.Matches(name, cfg.HeadsetOutputPreferred);
        }

        string Pick(string configured, string stateKey)
        {
            if (!string.IsNullOrEmpty(configured)) return configured;
            string v;
            return ReadState().TryGetValue(stateKey, out v) ? v : null;
        }

        static Dictionary<string, string> ReadState()
        {
            var d = new Dictionary<string, string>();
            try
            {
                foreach (var line in File.ReadAllLines(StatePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return d;
        }

        // The first version was called Alpha2AutoSwitch: carry its settings and autostart over once.
        static void MigrateFromAlpha2AutoSwitch()
        {
            try
            {
                string old = Path.Combine(Path.GetDirectoryName(DataDir), "Alpha2AutoSwitch");
                if (Directory.Exists(old) && !Directory.Exists(DataDir))
                {
                    Directory.CreateDirectory(DataDir);
                    foreach (var f in new[] { "config.ini", "state.ini" })
                        if (File.Exists(Path.Combine(old, f))) File.Copy(Path.Combine(old, f), Path.Combine(DataDir, f));
                }
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k != null && k.GetValue("Alpha2AutoSwitch") != null)
                    {
                        k.DeleteValue("Alpha2AutoSwitch", false);
                        k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    }
                }
            }
            catch { }
        }

        bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        void SetAutostart(bool enable)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunName, false);
            }
        }

        void Refresh()
        {
            var m = active;
            string state = monitors.Count == 0 ? "no headset sources enabled"
                : m == null ? "no headset found"
                : headsetOn == true ? "on"
                : headsetOn == false ? "off"
                : "waiting";
            var sb = new StringBuilder((m == null ? "HeadsetAutoSwitch" : m.Name) + ": " + state);
            if (battery >= 0 && headsetOn == true) sb.Append(" · " + battery + "%");
            statusItem.Text = sb.ToString() + (currentOutput != null ? "\nOutput: " + currentOutput : "");
            string tip = sb.ToString();
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

            Color c = m == null ? Color.FromArgb(200, 120, 40)
                : headsetOn == true ? Color.FromArgb(60, 180, 90)
                : Color.FromArgb(140, 140, 140);
            SetIcon(c);
        }

        // Small headphone glyph tinted by state: green = on, grey = off/unknown, orange = no headset found.
        void SetIcon(Color c)
        {
            int s = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(s, s))
            using (var g = Graphics.FromImage(bmp))
            using (var pen = new Pen(c, Math.Max(2f, s / 8f)))
            using (var brush = new SolidBrush(c))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float m = s * 0.12f, w = s - 2 * m;
                g.DrawArc(pen, m, m, w, w * 1.1f, 180, 180);
                float cupW = s * 0.26f, cupH = s * 0.42f, cupY = s * 0.5f;
                g.FillRectangle(brush, m - pen.Width / 2, cupY, cupW, cupH);
                g.FillRectangle(brush, s - m + pen.Width / 2 - cupW, cupY, cupW, cupH);
                IntPtr h = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(h);
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = h;
            }
        }

        protected override void ExitThreadCore()
        {
            Log.Write("exiting");
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (watcher != null) watcher.Dispose();
            foreach (var m in monitors) m.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
