using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HeadsetAutoSwitch;

/// <summary>What one <c>headsetcontrol -b -o json</c> run says about a headset.</summary>
/// <param name="Name">Device name, e.g. "SteelSeries Arctis Nova 7".</param>
/// <param name="Product">USB product string, usually what Windows endpoint names contain.</param>
/// <param name="On">True/false when HeadsetControl is sure; null for timeouts and other errors.</param>
/// <param name="Battery">Battery percentage, or -1 when unknown.</param>
internal sealed record HeadsetReading(string Name, string Product, bool? On, int Battery);

/// <summary>Parses HeadsetControl's JSON output (<c>headsetcontrol -b -o json</c>, API 1.x).</summary>
internal static class HeadsetControlOutput
{
    /// <summary>
    /// Returns the first headset with battery information that <paramref name="skip"/> doesn't
    /// exclude, or null when there is none. <paramref name="skip"/> gets the vendor and product id
    /// as HeadsetControl prints them ("0x03f0", "0x08be").
    /// </summary>
    /// <exception cref="SerializationException">The output isn't valid JSON.</exception>
    public static HeadsetReading? Parse(string json, Func<string, string, bool> skip)
    {
        var serializer = new DataContractJsonSerializer(typeof(Output), new DataContractJsonSerializerSettings
        {
            UseSimpleDictionaryFormat = true,
        });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var output = (Output?)serializer.ReadObject(stream);

        foreach (var device in output?.Devices ?? [])
        {
            if (device.Battery is null || skip(device.VendorId ?? "", device.ProductId ?? ""))
            {
                continue;
            }

            var name = device.Name ?? "Headset";
            var product = device.Product ?? "";
            return device.Battery.Status switch
            {
                "BATTERY_AVAILABLE" or "BATTERY_CHARGING" => new HeadsetReading(name, product, true, device.Battery.Level ?? -1),
                "BATTERY_UNAVAILABLE" when IsOff(device) => new HeadsetReading(name, product, false, -1),
                // A timeout or HID error says nothing either way.
                _ => new HeadsetReading(name, product, null, -1),
            };
        }

        return null;
    }

    // Drivers report a powered-off headset in one of two ways: most fail the battery query with
    // HeadsetControl's "offline" error; some (e.g. Audeze Maxwell, ASTRO A50 Gen 4) return
    // BATTERY_UNAVAILABLE successfully, with no error at all.
    private static bool IsOff(Device device)
    {
        if (device.Errors is null || !device.Errors.TryGetValue("battery", out var error) || string.IsNullOrEmpty(error))
        {
            return true;
        }

        return error.IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [DataContract]
    private sealed class Output
    {
        [DataMember(Name = "devices")] public List<Device>? Devices { get; set; }
    }

    [DataContract]
    private sealed class Device
    {
        [DataMember(Name = "device")] public string? Name { get; set; }
        [DataMember(Name = "product")] public string? Product { get; set; }
        [DataMember(Name = "id_vendor")] public string? VendorId { get; set; }
        [DataMember(Name = "id_product")] public string? ProductId { get; set; }
        [DataMember(Name = "battery")] public Battery? Battery { get; set; }
        [DataMember(Name = "errors")] public Dictionary<string, string>? Errors { get; set; }
    }

    [DataContract]
    private sealed class Battery
    {
        [DataMember(Name = "status")] public string? Status { get; set; }
        [DataMember(Name = "level")] public int? Level { get; set; }
    }
}
