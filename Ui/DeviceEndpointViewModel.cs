using System.ComponentModel;
using System.Runtime.CompilerServices;
using GKME.Core;
using GKME.Localization;

namespace GKME.Ui;

/// <summary>One network endpoint (IP) of a discovered device. A card may hold
/// several endpoints when the same MAC is reachable over multiple IPs.</summary>
public class DeviceEndpointViewModel : INotifyPropertyChanged
{
    private bool _isConnected;
    private bool _isReconnecting;
    private bool _isConnecting;

    public DeviceEndpointViewModel(DiscoveredDevice device)
    {
        Device = device;
    }

    public DiscoveredDevice Device { get; }
    public string IpAddress => Device.IpString;
    public string DisplayAddress => Device.DisplayAddress;
    public bool IsUsb => Device.IsUsb;

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            RaiseStateChanged();
        }
    }

    public bool IsReconnecting
    {
        get => _isReconnecting;
        set
        {
            if (_isReconnecting == value) return;
            _isReconnecting = value;
            RaiseStateChanged();
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set
        {
            if (_isConnecting == value) return;
            _isConnecting = value;
            RaiseStateChanged();
        }
    }

    public bool ShowConnect => !IsConnected && !IsReconnecting && !IsConnecting;
    public bool ShowControls => IsConnected;
    public bool ShowReconnecting => IsReconnecting;
    public bool IsBusy => IsConnected || IsConnecting || IsReconnecting;

    public string StatusText => IsConnected
        ? Localizer.Instance.Get("Status_Connected")
        : IsReconnecting || IsConnecting
            ? Localizer.Instance.Get("Status_Attempting")
            : Localizer.Instance.Get("Status_Available");

    public void RefreshLocalization() => OnPropertyChanged(nameof(StatusText));

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(ShowConnect));
        OnPropertyChanged(nameof(ShowControls));
        OnPropertyChanged(nameof(ShowReconnecting));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StatusText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
