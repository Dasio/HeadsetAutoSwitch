// Windows Core Audio: list endpoints, read/set the default device, and watch for endpoint changes.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HeadsetAutoSwitch
{
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p, p2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore { int GetCount(out int c); int GetAt(int i, out PROPERTYKEY k); int GetValue(ref PROPERTYKEY k, out PROPVARIANT v); }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { int Activate(); int OpenPropertyStore(int access, out IPropertyStore ps); int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id); }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection { int GetCount(out int c); int Item(int i, out IMMDevice d); }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection c);
        int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice d);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice d);
        int RegisterEndpointNotificationCallback(IMMNotificationClient c);
        int UnregisterEndpointNotificationCallback(IMMNotificationClient c);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
    }

    // Fires when endpoints appear/disappear or get enabled/disabled (e.g. NGENUITY starting).
    class EndpointWatcher : IMMNotificationClient
    {
        readonly IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        readonly Action changed;
        public EndpointWatcher(Action changed) { this.changed = changed; en.RegisterEndpointNotificationCallback(this); }
        public void Dispose() { en.UnregisterEndpointNotificationCallback(this); }
        public void OnDeviceStateChanged(string id, int state) { changed(); }
        public void OnDeviceAdded(string id) { changed(); }
        public void OnDeviceRemoved(string id) { changed(); }
        public void OnDefaultDeviceChanged(int flow, int role, string id) { }
        public void OnPropertyValueChanged(string id, PROPERTYKEY key) { }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    // Undocumented but stable since Vista; what every "set default device" tool uses.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(); int GetDeviceFormat(); int ResetDeviceFormat(); int SetDeviceFormat();
        int GetProcessingPeriod(); int SetProcessingPeriod(); int GetShareMode(); int SetShareMode();
        int GetPropertyValue(); int SetPropertyValue();
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class CPolicyConfigClient { }

    static class Audio
    {
        public const int Render = 0, Capture = 1;

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT v);

        static string NameOf(IMMDevice d)
        {
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            IPropertyStore ps;
            if (d.OpenPropertyStore(0, out ps) != 0) return null;
            PROPVARIANT v;
            if (ps.GetValue(ref key, out v) != 0) return null;
            string name = Marshal.PtrToStringUni(v.p);
            PropVariantClear(ref v);
            return name;
        }

        // Active endpoints only: name -> id
        public static Dictionary<string, string> List(int flow)
        {
            var res = new Dictionary<string, string>();
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(flow, 1, out col) != 0) return res;
            int n;
            col.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IMMDevice d;
                if (col.Item(i, out d) != 0) continue;
                string id, name = NameOf(d);
                d.GetId(out id);
                if (name != null && id != null) res[name] = id;
            }
            return res;
        }

        static IMMDevice Default(int flow)
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice d;
            return en.GetDefaultAudioEndpoint(flow, 0, out d) == 0 ? d : null;
        }

        public static string DefaultName(int flow) { var d = Default(flow); return d == null ? null : NameOf(d); }

        static string DefaultId(int flow)
        {
            var d = Default(flow);
            string id = null;
            if (d != null) d.GetId(out id);
            return id;
        }

        public static void SetDefault(string id)
        {
            var pc = (IPolicyConfig)new CPolicyConfigClient();
            for (int role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, role));
        }

        public static bool Matches(string name, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || name == null) return false;
            var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
            return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase);
        }

        // Returns the full name that was made default, or null if nothing matched.
        public static bool IsActive(int flow, string pattern)
        {
            foreach (var name in List(flow).Keys) if (Matches(name, pattern)) return true;
            return false;
        }

        public static string SetDefaultByPattern(int flow, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return null;
            foreach (var kv in List(flow))
                if (Matches(kv.Key, pattern))
                {
                    // Re-setting the current default still makes apps reopen their streams (audible gap).
                    if (kv.Value != DefaultId(flow)) SetDefault(kv.Value);
                    return kv.Key;
                }
            return null;
        }
    }

}
