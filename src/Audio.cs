using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HeadsetAutoSwitch;

internal enum AudioFlow
{
    Output = 0, // eRender
    Input = 1,  // eCapture
}

internal readonly record struct AudioDevice(string Id, string Name);

/// <summary>Windows Core Audio: list active endpoints and get/set the default device.</summary>
internal static class Audio
{
    private const int DeviceStateActive = 0x1;
    private const ushort VtLpwstr = 31;
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    /// <summary>Active endpoints in Windows' order.</summary>
    public static List<AudioDevice> List(AudioFlow flow)
    {
        var devices = new List<AudioDevice>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out var collection) != 0)
        {
            return devices;
        }

        collection.GetCount(out var count);
        for (var i = 0; i < count; i++)
        {
            if (collection.Item(i, out var device) == 0
                && device.GetId(out var id) == 0
                && NameOf(device) is { } name)
            {
                devices.Add(new AudioDevice(id, name));
            }
        }

        return devices;
    }

    public static AudioDevice? GetDefault(AudioFlow flow, Role role = Role.Console)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0
            || device.GetId(out var id) != 0
            || NameOf(device) is not { } name)
        {
            return null;
        }

        return new AudioDevice(id, name);
    }

    public static bool IsActive(AudioFlow flow, string pattern) =>
        Find(flow, pattern) is not null;

    public static AudioDevice? Find(AudioFlow flow, string pattern)
    {
        foreach (var device in List(flow))
        {
            if (DeviceName.Matches(device.Name, pattern))
            {
                return device;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes the first active device matching <paramref name="pattern"/> the default for every role
    /// (default device and communications device) and returns it, or returns null when nothing matches.
    /// </summary>
    public static AudioDevice? SetDefault(AudioFlow flow, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern) || Find(flow, pattern!) is not { } device)
        {
            return null;
        }

        var policy = (IPolicyConfig)new PolicyConfigClient();
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            // Setting the device that already is the default still makes applications reopen their
            // streams, which is an audible gap; roles can differ, so check each one.
            if (GetDefault(flow, role)?.Id != device.Id)
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(device.Id, role));
            }
        }

        return device;
    }

    public static AudioDevice? ById(string id)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        return enumerator.GetDevice(id, out var device) == 0 && NameOf(device) is { } name
            ? new AudioDevice(id, name)
            : null;
    }

    private static string? NameOf(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0)
        {
            return null;
        }

        var key = FriendlyName;
        if (store.GetValue(ref key, out var value) != 0)
        {
            return null;
        }

        try
        {
            return value.vt == VtLpwstr ? Marshal.PtrToStringUni(value.pointerValue) : null;
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

/// <summary>Reports endpoints being added, removed, enabled or disabled, and default device changes.</summary>
internal sealed class EndpointWatcher : IMMNotificationClient, IDisposable
{
    private readonly IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
    private readonly Action endpointsChanged;
    private readonly Action<AudioFlow, string> defaultChanged;

    /// <param name="endpointsChanged">Runs on a Windows audio thread; must return quickly.</param>
    /// <param name="defaultChanged">New default device id per flow; same threading rule.</param>
    public EndpointWatcher(Action endpointsChanged, Action<AudioFlow, string> defaultChanged)
    {
        this.endpointsChanged = endpointsChanged;
        this.defaultChanged = defaultChanged;
        Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(this));
    }

    public void Dispose() => enumerator.UnregisterEndpointNotificationCallback(this);

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, int newState) => endpointsChanged();
    void IMMNotificationClient.OnDeviceAdded(string deviceId) => endpointsChanged();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => endpointsChanged();

    void IMMNotificationClient.OnDefaultDeviceChanged(AudioFlow flow, Role role, string defaultDeviceId)
    {
        if (role == Role.Console && defaultDeviceId is not null)
        {
            defaultChanged(flow, defaultDeviceId);
        }
    }

    void IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) { }
}

internal enum Role
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid formatId, int propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly int PropertyId = propertyId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    private ushort reserved1, reserved2, reserved3;
    public IntPtr pointerValue;
    private IntPtr padding;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(AudioFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(AudioFlow dataFlow, Role role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(); // not used; keeps the vtable order
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDefaultDeviceChanged(AudioFlow flow, Role role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

/// <summary>
/// Undocumented, but stable since Windows Vista; it's what every "set default audio device" tool
/// uses, as Windows has no public API for it. Only <see cref="SetDefaultEndpoint"/> is called; the
/// other entries keep the vtable order.
/// </summary>
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int ResetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient;
