using Xunit;

namespace HeadsetAutoSwitch.Tests;

// The JSON below is real output of `headsetcontrol -b -o json` (4.1.0), trimmed to the fields that
// matter: the built-in test device (--test-device 0/4/5) and a HyperX Cloud Alpha 2 Wireless.
public class HeadsetControlOutputTests
{
    private const string Available = """
        {"name":"HeadsetControl","device_count":1,"devices":[{"status":"success","device":"HeadsetControl Test device",
         "product":"Test Device","id_vendor":"0xf00b","id_product":"0xa00c","capabilities_str":["battery"],
         "battery":{"status":"BATTERY_AVAILABLE","level":42,"voltage_mv":3650,"time_to_empty_min":302}}]}
        """;

    private const string Offline = """
        {"name":"HeadsetControl","device_count":1,"devices":[{"status":"partial","device":"HeadsetControl Test device",
         "product":"Test Device","id_vendor":"0xf00b","id_product":"0xa00c",
         "battery":{"status":"BATTERY_UNAVAILABLE","level":-1},"errors":{"battery":"Device is offline or not responding"}}]}
        """;

    private const string TimedOut = """
        {"name":"HeadsetControl","device_count":1,"devices":[{"status":"partial","device":"HeadsetControl Test device",
         "product":"Test Device","id_vendor":"0xf00b","id_product":"0xa00c",
         "battery":{"status":"BATTERY_UNAVAILABLE","level":-1},"errors":{"battery":"Operation timed out"}}]}
        """;

    private const string Alpha2Charging = """
        {"name":"HeadsetControl","device_count":1,"devices":[{"status":"success","device":"HyperX Cloud Alpha 2 Wireless",
         "vendor":"HP, Inc","product":"HyperX Cloud Alpha 2 Wireless Controller","id_vendor":"0x03f0","id_product":"0x08be",
         "battery":{"status":"BATTERY_CHARGING","level":92,"voltage_mv":4331}}]}
        """;

    // Audeze Maxwell / ASTRO A50 Gen 4 style: headset off reported as a successful UNAVAILABLE.
    private const string UnavailableWithoutError = """
        {"name":"HeadsetControl","device_count":1,"devices":[{"status":"success","device":"Audeze Maxwell",
         "product":"Maxwell","id_vendor":"0x3329","id_product":"0x4b19",
         "battery":{"status":"BATTERY_UNAVAILABLE","level":-1}}]}
        """;

    private const string NoDevices = """{"name":"HeadsetControl","device_count":0,"devices":[]}""";

    private static bool SkipNothing(string vendor, string product) => false;

    [Fact]
    public void BatteryReadingMeansOn()
    {
        var reading = HeadsetControlOutput.Parse(Available, SkipNothing);

        Assert.Equal(new HeadsetReading("HeadsetControl Test device", "Test Device", true, 42), reading);
    }

    [Fact]
    public void ChargingMeansOn()
    {
        var reading = HeadsetControlOutput.Parse(Alpha2Charging, SkipNothing);

        Assert.Equal(true, reading?.On);
        Assert.Equal(92, reading?.Battery);
    }

    [Fact]
    public void OfflineErrorMeansOff() =>
        Assert.Equal(false, HeadsetControlOutput.Parse(Offline, SkipNothing)?.On);

    [Fact]
    public void UnavailableWithoutErrorMeansOff() =>
        Assert.Equal(false, HeadsetControlOutput.Parse(UnavailableWithoutError, SkipNothing)?.On);

    [Fact]
    public void TimeoutGivesNoVerdict()
    {
        var reading = HeadsetControlOutput.Parse(TimedOut, SkipNothing);

        Assert.NotNull(reading);
        Assert.Null(reading.On);
    }

    [Fact]
    public void NoDevicesMeansNotPresent() =>
        Assert.Null(HeadsetControlOutput.Parse(NoDevices, SkipNothing));

    [Fact]
    public void SkippedDevicesAreIgnored() =>
        Assert.Null(HeadsetControlOutput.Parse(Alpha2Charging, (vendor, product) => vendor == "0x03f0" && product == "0x08be"));

    [Fact]
    public void InvalidJsonThrows() =>
        Assert.ThrowsAny<System.Runtime.Serialization.SerializationException>(() => HeadsetControlOutput.Parse("not json", SkipNothing));
}
