using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HeadsetAutoSwitch;

/// <summary>
/// HyperX Cloud Alpha 2 Wireless, read straight from the base station; no extra software needed.
/// </summary>
/// <remarks>
/// The base station is two USB devices: audio (03F0:0ABE), which Windows always sees, and a control
/// MCU (03F0:08BE). The MCU's vendor HID interface (interface 2, usage page 0xFF13) uses 64-byte
/// reports, byte 0 = command (report ID), byte 1 = sub-command:
/// <code>
///   FB 0A 01 / FB 0A 00      pushed when the headset links / unlinks
///   FB 13 00 &lt;level&gt;       pushed when the battery level changes
///   52 01 -> 53 01 &lt;on&gt;      query the link state
///   50 02 -> 51 02 &lt;level&gt;   query the battery (charge state, temperature and voltage follow)
/// </code>
/// We block on a read of that interface (no polling). The link state is queried whenever the device
/// is (re)opened, because the push only happens on change, and the battery whenever the headset
/// links (its reply while unlinked is not meaningful).
/// </remarks>
internal sealed class Alpha2Monitor : IHeadsetMonitor
{
    private const string InterfacePath = "vid_03f0&pid_08be&mi_02";
    private const int ReportSize = 64;

    public event Action<bool, bool>? ConnectionChanged;
    public event Action<int>? BatteryChanged;
    public event Action<bool>? PresenceChanged;

    public string Name => "Cloud Alpha 2";
    public string DefaultOutput => "*Cloud Alpha 2 Wireless Game*";
    public string DefaultMic => "*Cloud Alpha 2 Wireless Chat*";

    private volatile bool stopping;
    private volatile bool reading;          // a blocking read is in progress
    private volatile bool cancelRequested;  // ...and we cancelled it on purpose
    private volatile SafeFileHandle? handle;
    private volatile int linkState = -1;    // -1 unknown, 0 unlinked, 1 linked

    public bool? IsOn => linkState < 0 ? null : linkState == 1;

    public void Start() => new Thread(Run) { IsBackground = true, Name = nameof(Alpha2Monitor) }.Start();

    public void Dispose()
    {
        stopping = true;
        CancelRead();
    }

    // Abort the blocking read; Run() re-opens the device and queries the state again.
    public void Refresh() => CancelRead();

    private void CancelRead()
    {
        var h = handle;
        // Outside a read there's nothing to cancel: the next (re)open queries the state anyway.
        cancelRequested = reading;
        try
        {
            if (h is { IsInvalid: false, IsClosed: false })
            {
                Native.CancelIoEx(h, IntPtr.Zero);
            }
        }
        catch (ObjectDisposedException)
        {
            // Closed between the check and the call: there's no read left to cancel.
        }
    }

    private void Run()
    {
        bool? present = null;
        while (!stopping)
        {
            var path = FindInterfacePath();
            using var h = path is null ? null : Native.OpenDevice(path, Native.GenericRead);
            var found = h is { IsInvalid: false };
            if (present != found)
            {
                present = found;
                Log.Write(found ? "Cloud Alpha 2: base station found" : "Cloud Alpha 2: base station not found, waiting");
                PresenceChanged?.Invoke(found);
            }

            if (!found)
            {
                Thread.Sleep(5000);
                continue;
            }

            handle = h;
            try
            {
                // Input reports are queued per open handle, so the reply lands in ours.
                linkState = -1;
                cancelRequested = false;
                Send(path!, 0x52, 0x01);
                reading = true;
                ReadReports(path!, h!);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // .NET Framework reports a read cancelled by Stop/Refresh (e.g. on resume from sleep)
                // as OperationCanceledException; an IOException means the base station was unplugged
                // or the handle went stale across sleep.
                if (!cancelRequested)
                {
                    Log.Write("Cloud Alpha 2: lost the base station: " + ex.Message);
                }
            }
#pragma warning disable CA1031 // A monitor must never take the app down; log it and reconnect.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Log.Write("Cloud Alpha 2: unexpected error, reconnecting: " + ex);
            }
            finally
            {
                reading = false;
                handle = null;
            }

            if (!stopping)
            {
                Thread.Sleep(1000);
            }
        }
    }

    private void ReadReports(string path, SafeFileHandle h)
    {
        using var stream = new FileStream(h, FileAccess.Read, ReportSize, isAsync: false);
        var report = new byte[ReportSize];
        while (!stopping)
        {
            // NGENUITY, when running, polls this interface ~75 times a second and Windows delivers
            // every input report to every open handle: skip everything we don't need.
            if (stream.Read(report, 0, report.Length) < 4)
            {
                continue;
            }

            switch (report[0], report[1])
            {
                case (0xFB, 0x0A): // link changed
                    OnLink(path, report[2] != 0, initial: false);
                    break;
                case (0x53, 0x01): // reply to 52 01
                    OnLink(path, report[2] != 0, initial: true);
                    break;
                case (0xFB, 0x13) when linkState == 1 && report[3] <= 100: // battery level changed
                    BatteryChanged?.Invoke(report[3]);
                    break;
                case (0x51, 0x02) when linkState == 1 && report[2] <= 100: // reply to 50 02 (ours or NGENUITY's)
                    BatteryChanged?.Invoke(report[2]);
                    break;
            }
        }
    }

    // Replies to other programs' 52 01 queries arrive here too, so act only on an actual change.
    private void OnLink(string path, bool isLinked, bool initial)
    {
        var previous = linkState;
        var state = isLinked ? 1 : 0;
        if (previous == state)
        {
            return;
        }

        linkState = state;
        ConnectionChanged?.Invoke(isLinked, initial || previous < 0);
        if (isLinked)
        {
            Send(path, 0x50, 0x02);
        }
    }

    private static void Send(string path, byte command, byte subCommand)
    {
        using var h = Native.OpenDevice(path, Native.GenericWrite);
        if (h.IsInvalid)
        {
            throw new IOException("cannot open the base station for writing", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var report = new byte[ReportSize];
        report[0] = command;
        report[1] = subCommand;
        if (!Native.WriteFile(h, report, report.Length, out _, IntPtr.Zero))
        {
            throw new IOException($"query {command:X2} {subCommand:X2} failed", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private static string? FindInterfacePath()
    {
        Native.HidD_GetHidGuid(out var hidGuid);
        var set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, Native.DigcfPresent | Native.DigcfDeviceInterface);
        if (set == Native.InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var data = new Native.SpDeviceInterfaceData { cbSize = Marshal.SizeOf<Native.SpDeviceInterfaceData>() };
            for (var i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data); i++)
            {
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: DWORD + one WCHAR, padded on x64.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(detail + 4);
                        if (path?.IndexOf(InterfacePath, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return path;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }

        return null;
    }

    private static class Native
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        private const uint FileShareReadWrite = 0x1 | 0x2;
        private const uint OpenExisting = 3;
        public const int DigcfPresent = 0x2;
        public const int DigcfDeviceInterface = 0x10;
        public static readonly IntPtr InvalidHandleValue = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct SpDeviceInterfaceData
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        // Other programs (NGENUITY, HeadsetControl) keep the device open too, so always share.
        public static SafeFileHandle OpenDevice(string path, uint access) =>
            CreateFile(path, access, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        public static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
    }
}
