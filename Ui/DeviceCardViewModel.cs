using System.ComponentModel;
using System.Runtime.CompilerServices;
using GamepadEmu.Protocol;
using GKME.Core;

namespace GKME.Ui;

public class DeviceCardViewModel : INotifyPropertyChanged
{
    private readonly DiscoveredDevice _device;
    private bool _isConnected;
    private bool _isReconnecting;
    private bool _isConnecting;
    private bool _isRestoringMode;
    private ControllerMode _mode = ControllerMode.Ds4;

    public string IpAddress => _device.IpString;
    public string DeviceName => _device.DeviceName;

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowConnect));
            OnPropertyChanged(nameof(ShowControls));
            OnPropertyChanged(nameof(ShowReconnecting));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsReconnecting
    {
        get => _isReconnecting;
        set
        {
            if (_isReconnecting == value) return;
            _isReconnecting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowConnect));
            OnPropertyChanged(nameof(ShowControls));
            OnPropertyChanged(nameof(ShowReconnecting));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set
        {
            if (_isConnecting == value) return;
            _isConnecting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowConnect));
            OnPropertyChanged(nameof(ShowControls));
            OnPropertyChanged(nameof(ShowReconnecting));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool ShowConnect => !IsConnected && !IsReconnecting && !IsConnecting;
    public bool ShowControls => IsConnected;
    public bool ShowReconnecting => IsReconnecting;

    public string StatusText => IsConnected ? "已连接" : IsReconnecting || IsConnecting ? "尝试连接" : "可连接";

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

    internal DiscoveredDevice Device => _device;

    public static List<ControllerMode> AllModes { get; } = new()
    {
        ControllerMode.Ds4,
        ControllerMode.Xbox360,
        ControllerMode.Dualsense,
    };

    public static string DisplayName(ControllerMode mode) => mode switch
    {
        ControllerMode.Ds4 => "DualShock 4",
        ControllerMode.Xbox360 => "Xbox 360",
        ControllerMode.Dualsense => "DualSense",
        _ => "?",
    };

    public DeviceCardViewModel(DiscoveredDevice device)
    {
        _device = device;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
