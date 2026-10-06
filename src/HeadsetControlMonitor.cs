// Any headset HeadsetControl (https://github.com/Sapd/HeadsetControl) can read the battery of.
//
// Most wireless headsets only answer when asked, so we run `headsetcontrol -b -o json` every few
// seconds. A battery reading means the headset is on; HeadsetControl's "offline" error means the
// dongle is there but the headset is off. Timeouts and other errors are ignored (no switch).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace HeadsetAutoSwitch
{
    interface IHeadsetMonitor
    {
        event Action<bool, bool> ConnectionChanged;   // (headset on, state read at startup rather than a change)
        event Action<int> BatteryChanged;             // percent
        event Action<bool> PresenceChanged;           // dongle / base station found
        string Name { get; }
        string DefaultOutput { get; }                 // device name pattern used when the setting is empty
        string DefaultMic { get; }
        void Start();
        void Stop();
        void Reopen();                                // e.g. after resume from sleep
    }

    class HeadsetControlMonitor : IHeadsetMonitor
    {
        public event Action<bool, bool> ConnectionChanged;
        public event Action<int> BatteryChanged;
        public event Action<bool> PresenceChanged;

        readonly string exe, extraArgs;
        readonly int intervalMs;
        readonly Func<string, string, bool> skip;      // (vendor id, product id) handled elsewhere
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool stopping;
        volatile string name = "Headset", product = "";

        public HeadsetControlMonitor(string exe, string extraArgs, int intervalSeconds, Func<string, string, bool> skip)
        {
            this.exe = exe;
            this.extraArgs = extraArgs ?? "";
            this.intervalMs = Math.Max(2, intervalSeconds) * 1000;
            this.skip = skip;
        }

        public string Name { get { return name; } }
        public string DefaultOutput { get { return product.Length > 0 ? "*" + product + "*" : ""; } }
        public string DefaultMic { get { return product.Length > 0 ? "*" + product + "*" : ""; } }

        // Looks next to the app, in the app's data folder, then on PATH. Null if not found.
        public static string Find(string dataDir)
        {
            var candidates = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "headsetcontrol.exe"),
                Path.Combine(dataDir, "headsetcontrol.exe"),
            };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim(), "headsetcontrol.exe"));
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "HeadsetControlMonitor" };
            t.Start();
        }

        public void Stop() { stopping = true; wake.Set(); }
        public void Reopen() { wake.Set(); }

        void Run()
        {
            bool? present = null, on = null;
            int offlineReadings = 0;
            Log.Write("HeadsetControl: using " + exe);
            while (!stopping)
            {
                Reading r = Poll();
                bool found = r != null;
                if (present != found)
                {
                    present = found;
                    Log.Write(found ? "HeadsetControl: found " + r.Name : "HeadsetControl: no supported headset, waiting");
                    if (found) { name = r.Name; product = r.Product; }
                    if (PresenceChanged != null) PresenceChanged(found);
                }

                if (found && r.On.HasValue)
                {
                    // One "offline" could be a hiccup while the headset reconnects; wait for a second one.
                    offlineReadings = r.On.Value ? 0 : offlineReadings + 1;
                    bool confirmed = r.On.Value || offlineReadings >= 2 || on == null;
                    if (confirmed && on != r.On.Value)
                    {
                        bool first = on == null;
                        on = r.On.Value;
                        if (ConnectionChanged != null) ConnectionChanged(on.Value, first);
                    }
                    if (r.Battery >= 0 && BatteryChanged != null) BatteryChanged(r.Battery);
                }
                wake.WaitOne(intervalMs);
            }
        }

        class Reading
        {
            public string Name, Product;
            public bool? On;          // null = no verdict (timeout, other error)
            public int Battery = -1;
        }

        Reading Poll()
        {
            string json = RunHeadsetControl();
            if (json == null) return null;
            try
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                object devices;
                if (!root.TryGetValue("devices", out devices) || !(devices is IEnumerable)) return null;
                foreach (var item in (IEnumerable)devices)
                {
                    var dev = item as Dictionary<string, object>;
                    if (dev == null || skip(Str(dev, "id_vendor"), Str(dev, "id_product"))) continue;
                    object batteryObj;
                    if (!dev.TryGetValue("battery", out batteryObj)) continue;
                    var battery = batteryObj as Dictionary<string, object>;
                    if (battery == null) continue;

                    var r = new Reading { Name = Str(dev, "device"), Product = Str(dev, "product") };
                    string status = Str(battery, "status");
                    if (status == "BATTERY_AVAILABLE" || status == "BATTERY_CHARGING")
                    {
                        r.On = true;
                        object level;
                        if (battery.TryGetValue("level", out level) && level is int) r.Battery = (int)level;
                    }
                    else if (BatteryError(dev).IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        r.On = false;
                    }
                    return r;
                }
            }
            catch (Exception ex) { Log.Write("HeadsetControl: unreadable output: " + ex.Message); }
            return null;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
        }

        static string BatteryError(Dictionary<string, object> dev)
        {
            object errors;
            var e = dev.TryGetValue("errors", out errors) ? errors as Dictionary<string, object> : null;
            return e == null ? "" : Str(e, "battery");
        }

        string RunHeadsetControl()
        {
            try
            {
                var psi = new ProcessStartInfo(exe, ("-b -o json " + extraArgs).Trim())
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } Log.Write("HeadsetControl: no answer within 15 s"); return null; }
                    return output.Result;
                }
            }
            catch (Exception ex) { Log.Write("HeadsetControl: cannot run: " + ex.Message); return null; }
        }
    }
}
