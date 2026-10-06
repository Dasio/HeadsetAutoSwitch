// HyperX Cloud Alpha 2 Wireless monitor.
//
// The base station stays plugged in, so Windows never sees the headset disappear. Its MCU
// (USB 03F0:08BE, interface 2, usage page 0xFF13) does however push an input report the moment
// the headset links or unlinks:  FB 0A 01 = headset on,  FB 0A 00 = headset off.
// We block on that interface (no polling).
//
// Reports are 64 bytes, byte0 = command / report ID, byte1 = sub-command:
//   52 01        -> 53 01 <connected>          query headset link state (used at startup)
//   50 02        -> 51 02 <battery %> <charging> <temp i16> <mV u16> ...
//   FF 01 .. <cmd> <sub> at offset 14 = ACK,   FF 02 = NACK,   FB xx = unsolicited notification

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HeadsetAutoSwitch
{
    // HyperX Cloud Alpha 2 Wireless, talking to the base station directly (no extra software needed).
    // Instant: the base station pushes FB 0A 01/00 when the headset links/unlinks.
    class Alpha2Monitor : IHeadsetMonitor
    {
        const string InterfaceMatch = "vid_03f0&pid_08be&mi_02";

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
        [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle h, IntPtr o);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] b, int n, out int w, IntPtr o);

        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr reserved; }

        public event Action<bool, bool> ConnectionChanged;
        public event Action<int> BatteryChanged;
        public event Action<bool> PresenceChanged;

        public string Name { get { return "Cloud Alpha 2"; } }
        public string DefaultOutput { get { return "*Cloud Alpha 2 Wireless Game*"; } }
        public string DefaultMic { get { return "*Cloud Alpha 2 Wireless Chat*"; } }

        volatile bool stopping;
        volatile SafeFileHandle handle;

        static string FindPath()
        {
            Guid g;
            HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == new IntPtr(-1)) return null;
            try
            {
                var d = new SP_DEVICE_INTERFACE_DATA();
                d.cbSize = Marshal.SizeOf(d);
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
                {
                    int req;
                    SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(buf + 4);
                        if (path != null && path.ToLowerInvariant().Contains(InterfaceMatch)) return path;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "Alpha2Monitor" };
            t.Start();
        }

        public void Stop() { stopping = true; Reopen(); }

        // Abort the blocking read so Run() re-enumerates (used after resume from sleep).
        public void Reopen()
        {
            var h = handle;
            if (h != null && !h.IsInvalid) CancelIoEx(h, IntPtr.Zero);
        }

        static void Query(string path, byte cmd, byte sub)
        {
            using (var w = CreateFile(path, 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (w.IsInvalid) { Log.Write("cannot open base station for writing"); return; }
                var buf = new byte[64];
                buf[0] = cmd; buf[1] = sub;
                int written;
                if (!WriteFile(w, buf, buf.Length, out written, IntPtr.Zero))
                    Log.Write("query " + cmd.ToString("X2") + " failed: " + Marshal.GetLastWin32Error());
            }
        }

        void Run()
        {
            bool? present = null;
            while (!stopping)
            {
                string path = FindPath();
                SafeFileHandle h = path == null ? null : CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                bool ok = h != null && !h.IsInvalid;
                if (present != ok)
                {
                    present = ok;
                    Log.Write(ok ? "base station found" : "base station not found, waiting");
                    if (PresenceChanged != null) PresenceChanged(ok);
                }
                if (!ok) { Thread.Sleep(5000); continue; }

                handle = h;
                // Events only fire on change, so ask for the current state once per (re)open.
                // Replies are queued for every open handle, so it is fine to write before reading.
                Query(path, 0x52, 0x01);
                Query(path, 0x50, 0x02);
                try
                {
                    using (var fs = new FileStream(h, FileAccess.Read, 64, false))
                    {
                        var buf = new byte[64];
                        while (!stopping)
                        {
                            int n = fs.Read(buf, 0, buf.Length);
                            // NGENUITY polls this interface ~75x/s and Windows hands every input report
                            // to every open handle; we only care about two report types.
                            if (n >= 3 && buf[0] == 0xFB && buf[1] == 0x0A)
                            {
                                if (ConnectionChanged != null) ConnectionChanged(buf[2] != 0, false);
                            }
                            else if (n >= 3 && buf[0] == 0x53 && buf[1] == 0x01)
                            {
                                if (ConnectionChanged != null) ConnectionChanged(buf[2] != 0, true);
                            }
                            else if (n >= 3 && buf[0] == 0x51 && buf[1] == 0x02 && buf[2] <= 100)
                            {
                                if (BatteryChanged != null) BatteryChanged(buf[2]);
                            }
                        }
                    }
                }
                catch (Exception) { }
                handle = null;
                if (!stopping) Thread.Sleep(2000);
            }
        }
    }
}
