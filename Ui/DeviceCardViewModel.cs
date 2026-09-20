using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GamepadEmu.Protocol;
using GKME.Core;

namespace GKME.Ui;

/// <summary>A device card groups all endpoints (IPs) that share the same MAC.
/// When more than one endpoint is available and none is connected the card
/// expands into a larger layout listing every IP with its own connect button;
/// once one endpoint is used the card collapses back to the normal size.</summary>
public class DeviceCardViewModel : INotifyPropertyChanged
{
    private string _deviceName = "";
    private string _macAddress = "";
    private bool _isRestoringMode;
    private ControllerMode _mode = ControllerMode.Ds4;

    public ObservableCollection<DeviceEndpointViewModel> Endpoints { get; } = new();

    public DeviceCardViewModel(DiscoveredDevice device)
    {
        AddOrUpdateEndpoint(device);
    }

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (_deviceName == value) return;
            _deviceName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(HasSubtitle));
        }
    }

    public string MacAddress
    {
        get => _macAddress;
        set
        {
            if (_macAddress == value) return;
            _macAddress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeKey));
        }
    }

    /// <summary>Stable identity of the physical device (MAC for WiFi, adb serial
    /// for USB). Used to look up the card as endpoints come and go.</summary>
    public string GroupKey => Endpoints.Count > 0 ? Endpoints[0].Device.GroupKey : "";

    /// <summary>Persistence key for the emulated controller type.</summary>
    public string ModeKey => GroupKey;

    public bool HasMultipleEndpoints => Endpoints.Count > 1;

    /// <summary>Large layout while several IPs are available and none is in use.</summary>
    public bool IsExpanded => HasMultipleEndpoints && !Endpoints.Any(e => e.IsBusy);
    public bool IsCollapsed => !IsExpanded;

    public DeviceEndpointViewModel? ActiveEndpoint =>
        Endpoints.FirstOrDefault(e => e.IsBusy) ?? Endpoints.FirstOrDefault();

    public string ActiveIpAddress => ActiveEndpoint?.DisplayAddress ?? "";
    public string Title => string.IsNullOrWhiteSpace(DeviceName) ? ActiveIpAddress : DeviceName;

    public string Subtitle => IsExpanded
        ? $"{Endpoints.Count} 个可用连接"
        : string.IsNullOrWhiteSpace(DeviceName) ? "" : ActiveIpAddress;
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public bool ShowConnect => ActiveEndpoint?.ShowConnect ?? false;
    public bool ShowControls => ActiveEndpoint?.ShowControls ?? false;
    public bool ShowReconnecting => ActiveEndpoint?.ShowReconnecting ?? false;
    public string StatusText => ActiveEndpoint?.StatusText ?? "可连接";

    public ControllerMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            OnPropertyChanged();
        }
    }

    // True while Mode is being set programmatically (session restore/reset).
    // Lets the UI skip persisting these changes, so a saved mode isn't
    // overwritten by the default value.
    internal bool IsRestoringMode => _isRestoringMode;

    internal void SetModeRestored(ControllerMode mode)
    {
        _isRestoringMode = true;
        try { Mode = mode; }
        finally { _isRestoringMode = false; }
    }

    public static List<ControllerMode> AllModes { get; } = new()
    {
        ControllerMode.Ds4,
        ControllerMode.Xbox360,
        ControllerMode.Dualsense,
        ControllerMode.SwitchPro,
    };

    public static string DisplayName(ControllerMode mode) => mode switch
    {
        ControllerMode.Ds4 => "DualShock 4",
        ControllerMode.Xbox360 => "Xbox 360",
        ControllerMode.Dualsense => "DualSense",
        ControllerMode.SwitchPro => "Switch Pro",
        _ => "?",
    };

    public DeviceEndpointViewModel AddOrUpdateEndpoint(DiscoveredDevice device)
    {
        var existing = FindEndpoint(device.IpString);
        if (existing != null) return existing;

        var ep = new DeviceEndpointViewModel(device);
        ep.PropertyChanged += OnEndpointPropertyChanged;
        Endpoints.Add(ep);

        if (!string.IsNullOrWhiteSpace(device.DeviceName)) DeviceName = device.DeviceName;
        if (!string.IsNullOrWhiteSpace(device.MacAddress)) MacAddress = device.MacAddress;
        RaiseGroupingChanged();
        return ep;
    }

    public void RemoveEndpoint(DeviceEndpointViewModel ep)
    {
        ep.PropertyChanged -= OnEndpointPropertyChanged;
        Endpoints.Remove(ep);
        RaiseGroupingChanged();
    }

    public DeviceEndpointViewModel? FindEndpoint(string ip) =>
        Endpoints.FirstOrDefault(e => e.IpAddress == ip);

    private void OnEndpointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceEndpointViewModel.IsConnected)
            or nameof(DeviceEndpointViewModel.IsConnecting)
            or nameof(DeviceEndpointViewModel.IsReconnecting))
        {
            RaiseGroupingChanged();
        }
    }

    private void RaiseGroupingChanged()
    {
        OnPropertyChanged(nameof(ModeKey));
        OnPropertyChanged(nameof(HasMultipleEndpoints));
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(IsCollapsed));
        OnPropertyChanged(nameof(ActiveEndpoint));
        OnPropertyChanged(nameof(ActiveIpAddress));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasSubtitle));
        OnPropertyChanged(nameof(ShowConnect));
        OnPropertyChanged(nameof(ShowControls));
        OnPropertyChanged(nameof(ShowReconnecting));
        OnPropertyChanged(nameof(StatusText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
