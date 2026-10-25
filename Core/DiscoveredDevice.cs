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

    /// <summary>Endpoint and device identity of this device, plus its display
    /// forms. Computed on demand because the Hello handshake can fill in the MAC
    /// (and so change <see cref="DeviceIdentity.DeviceId"/>) after discovery.</summary>
    public DeviceIdentity Identity => DeviceIdentity.Of(this);
}
