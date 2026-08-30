using System.Net;

namespace GKME.Core;

public class DiscoveredDevice
{
    public required string DeviceName { get; set; }
    public required IPAddress IpAddress { get; init; }
    public string IpString => IpAddress.ToString();
    public int Port { get; init; } = 37284;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string Key => $"{IpAddress}:{Port}";

    // Persisted-mode key: real device name for discovered devices, IP for
    // manual connections that have no name.
    public string ModeKey => string.IsNullOrWhiteSpace(DeviceName) || DeviceName == IpString
        ? IpString
        : DeviceName;
}
