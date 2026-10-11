using System.Net;

namespace GKME.Core;

public class DiscoveredDevice
{
    public string DeviceName { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public required IPAddress IpAddress { get; init; }
    public int Port { get; init; } = 37284;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>True when this device is reached over USB/ADB instead of the
    /// WiFi UDP transport. <see cref="IpAddress"/> is loopback and
    /// <see cref="Port"/> holds the adb-assigned local forward port.</summary>
    public bool IsUsb { get; init; }

    /// <summary>True when this device is a web browser client served by the
    /// embedded GKME-Web server. Its input arrives over the WebSocket bridge
    /// instead of UDP/USB; <see cref="MacAddress"/> holds the browser's stable
    /// client id.</summary>
    public bool IsWeb { get; init; }

    /// <summary>adb device serial; stable identity for a USB device.</summary>
    public string? AdbSerial { get; init; }

    /// <summary>Raw adb state (device / unauthorized / offline).</summary>
    public string? AdbState { get; set; }

    public string IpString => IsWeb
        ? $"WEB:{MacAddress}"
        : IsUsb ? $"USB:{AdbSerial}" : IpAddress.ToString();

    /// <summary>Address shown in the UI. USB devices show their serial rather
    /// than the meaningless loopback address; web clients show the browser's
    /// real peer address.</summary>
    public string DisplayAddress => IsUsb ? $"USB {AdbSerial}" : IpAddress.ToString();

    // Endpoint identity: unique per transport endpoint.
    public string Key => IsWeb
        ? $"web:{MacAddress}"
        : IsUsb ? $"usb:{AdbSerial}" : $"{IpAddress}:{Port}";

    // Identity key: the MAC when known (stable across IP changes), otherwise the
    // IP. Used to group multiple endpoints that belong to the same physical
    // phone. USB devices use their adb serial so the card stays stable even
    // after the Hello handshake fills in the phone's ANDROID_ID.
    public string GroupKey => IsWeb
        ? $"web:{MacAddress}"
        : IsUsb
            ? $"usb:{AdbSerial}"
            : (string.IsNullOrWhiteSpace(MacAddress) ? IpString : MacAddress);

    // Persisted-mode key. The emulated controller type is stored per physical
    // device, so it follows the MAC rather than the (volatile) IP.
    public string ModeKey => GroupKey;
}
