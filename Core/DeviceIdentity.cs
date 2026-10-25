using System.Net;

namespace GKME.Core;

/// <summary>
/// The naming of one reachable phone, and the only place it is defined. Two kinds
/// of identity live here — <see cref="EndpointId"/>, which names a single
/// transport endpoint, and <see cref="DeviceId"/>, which names the physical
/// device behind it — plus the two human-facing forms the UI shows. USB, Web and
/// WiFi differ only in how those four strings are spelled, and that spelling is
/// written once, here.
///
/// Every consumer shares this pair: the session ledger
/// (<see cref="SessionIntake"/>) keys by <see cref="EndpointId"/>, inbound packet
/// routing builds the same <see cref="EndpointId"/>, device cards group and merge
/// by <see cref="DeviceId"/>, and the mode store persists by <see cref="DeviceId"/>.
/// Before this module a single session was keyed by two different strings in two
/// maps, and equality was never defined anywhere — it merely happened to be
/// spelled the same.
/// </summary>
public readonly record struct DeviceIdentity(
    string EndpointId, string DeviceId, string DisplayAddress, string EndpointLabel)
{
    /// <summary>Endpoints of one physical phone share a <see cref="DeviceId"/> but
    /// have distinct <see cref="EndpointId"/>s.</summary>
    public bool SameDevice(DeviceIdentity other) =>
        string.Equals(DeviceId, other.DeviceId, StringComparison.Ordinal);

    public static string UsbEndpointId(string serial) => $"usb:{serial}";
    public static string WebEndpointId(string clientId) => $"web:{clientId}";

    /// <summary>Endpoint id of a UDP-connected phone, as inbound packets spell it
    /// (routing must agree with the ledger, so both call this).</summary>
    public static string NetworkEndpointId(IPAddress address, int port) => $"{address}:{port}";

    public static DeviceIdentity Of(DiscoveredDevice device)
    {
        if (device.IsWeb)
        {
            var id = WebEndpointId(device.MacAddress);
            // The address shown is the browser's peer address; the ids are the
            // stable client id it was assigned at WebSocket connect.
            return new(id, id, device.IpAddress.ToString(), $"WEB:{device.MacAddress}");
        }

        if (device.IsUsb)
        {
            var id = UsbEndpointId(device.AdbSerial ?? "");
            return new(id, id, $"USB {device.AdbSerial}", $"USB:{device.AdbSerial}");
        }

        // WiFi: the endpoint is the ip:port the phone's UDP socket reaches the PC
        // from; the device is the MAC once the Hello handshake reveals it (stable
        // across IP changes), falling back to the bare IP before that.
        var ip = device.IpAddress.ToString();
        var deviceId = string.IsNullOrWhiteSpace(device.MacAddress) ? ip : device.MacAddress;
        return new(NetworkEndpointId(device.IpAddress, device.Port), deviceId, ip, ip);
    }
}
