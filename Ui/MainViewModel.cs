using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GKME.Core;
using GKME.Localization;

namespace GKME.Ui;

/// <summary>
/// The window's bindings, and nothing else. The device board (cards, endpoints,
/// session ledger, grouping/merge) lives in <see cref="DeviceBoard"/>, the
/// advertised web address in <see cref="WebAccess"/>, and the dialogs in the
/// shell (<see cref="MainWindow"/>). This type forwards network events into the
/// board, republishes its signals as bindings, and keeps the status line.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly NetworkService? _network;
    private readonly DeviceBoard _board;
    private readonly WebAccess _web = new();
    private string _statusText = "";
    private string? _statusKey = "Status_Ready";
    private object[] _statusArgs = System.Array.Empty<object>();
    private bool _driverOk;
    private string _manualIp = "";

    /// <summary>Raised when the shell should show the reboot prompt.</summary>
    public event Action? RebootRequired;
    /// <summary>Raised when the shell should show the USBip install prompt.</summary>
    public event Action? UsbipInstallRequired;
    /// <summary>Raised when discovery failed because another instance holds the port.</summary>
    public event Action? AlreadyRunning;

    public MainViewModel(NetworkService? network)
    {
        _network = network;
        _board = new DeviceBoard(network, new DeviceModeStore());
        _board.Changed += () => OnPropertyChanged(nameof(HasActiveSession));
        _board.Status += SetStatus;
        _web.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName switch
        {
            nameof(WebAccess.Selected) => nameof(SelectedWebInterface),
            nameof(WebAccess.Host) => nameof(WebHost),
            nameof(WebAccess.QrCode) => nameof(WebQrCode),
            nameof(WebAccess.HasUrl) => nameof(HasWebUrl),
            var other => other,
        });

        SetStatus("Status_Ready");

        if (_network == null)
        {
            _driverOk = false;
            SetStatus("Status_DriverCreateFailed");
            return;
        }

        _driverOk = true;
        if (!_network.HidMaestroAvailable)
            SetStatus("Status_HidMaestroUnavailable");

        _network.OnDeviceDiscovered += OnDeviceDiscovered;
        _network.OnDeviceLost += OnDeviceLost;
        _network.OnDeviceInfoUpdated += OnDeviceInfoUpdated;
        _network.OnSessionStarted += OnSessionStarted;
        _network.OnSessionEnded += OnSessionEnded;
        _network.OnSessionConnectionLost += OnSessionConnectionLost;
        _network.OnRebootRequired += OnRebootRequired;
        _network.OnUsbipInstallRequired += OnUsbipInstallRequired;
        _network.OnControllerError += OnControllerError;
    }

    // ----- device board bindings ------------------------------------------

    public ObservableCollection<DeviceCardViewModel> DeviceCards => _board.Cards;
    public ObservableCollection<GamepadSession> Sessions => _board.Sessions;
    public bool HasActiveSession => _board.HasActiveSession;

    // ----- web entry bindings ---------------------------------------------

    /// <summary>List of usable LAN interfaces for the web controller.</summary>
    public ObservableCollection<LanInterfaceInfo> WebInterfaces => _web.Interfaces;

    /// <summary>Selected interface; drives the advertised URL and QR code.</summary>
    public LanInterfaceInfo? SelectedWebInterface
    {
        get => _web.Selected;
        set => _web.Selected = value;
    }

    /// <summary>Current advertised host, e.g. "http://192.168.1.5:37285".</summary>
    public string WebHost => _web.Host;

    /// <summary>QR code pointing at <see cref="WebUrl"/>.</summary>
    public Bitmap? WebQrCode => _web.QrCode;

    public bool HasWebUrl => _web.HasUrl;

    /// <summary>Populates the interface list and selects a sensible default.
    /// Called once the embedded web server is up.</summary>
    public void ConfigureWeb() => _web.Configure();

    // ----- manual address / status line -----------------------------------

    public string ManualIp
    {
        get => _manualIp;
        set { _manualIp = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>Updates the status line and remembers the localization key so
    /// the text can be rebuilt when the language changes.</summary>
    public void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        StatusText = Localizer.Instance.Format(key, args);
    }

    /// <summary>Called by <see cref="App"/> when the embedded web server fails.</summary>
    public void SetWebServerError(string error) => SetStatus("Status_WebServerNotStarted", error);

    /// <summary>Re-raises every localized UI string after a language change.</summary>
    public void RefreshLocalization()
    {
        if (_statusKey != null)
            StatusText = Localizer.Instance.Format(_statusKey, _statusArgs);

        foreach (var card in _board.Cards)
            card.RefreshLocalization();
    }

    // ----- lifecycle ------------------------------------------------------

    public Task<bool> InitializeAsync()
    {
        if (_driverOk)
            StartAutoScan();

        // Any transport action detected during startup raised its event
        // before this view model existed; pick the sticky flags up now that
        // the window is up. Reboot takes precedence over install.
        if (_network?.RebootRequired == true)
            OnRebootRequired();
        else if (_network?.UsbipInstallRequired == true)
            OnUsbipInstallRequired();

        return Task.FromResult(_driverOk);
    }

    public void StartAutoScan()
    {
        if (!_driverOk || _network == null) return;
        try
        {
            SetStatus("Status_Scanning");
            _network.StartDiscovery();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.NativeErrorCode == 10048)
        {
            SetStatus("Status_AlreadyRunning");
            UiInvoke(() => AlreadyRunning?.Invoke());
        }
    }

    public async Task ShutdownAsync()
    {
        if (_network == null) return;
        _network.OnDeviceDiscovered -= OnDeviceDiscovered;
        _network.OnDeviceLost -= OnDeviceLost;
        _network.OnDeviceInfoUpdated -= OnDeviceInfoUpdated;
        _network.OnSessionStarted -= OnSessionStarted;
        _network.OnSessionEnded -= OnSessionEnded;
        _network.OnSessionConnectionLost -= OnSessionConnectionLost;
        _network.OnRebootRequired -= OnRebootRequired;
        _network.OnUsbipInstallRequired -= OnUsbipInstallRequired;
        _network.OnControllerError -= OnControllerError;
        _network.StopDiscovery();
        await _network.DisconnectAllAsync();
    }

    // ----- connect / disconnect / mode commands ---------------------------

    public Task ConnectAsync(DeviceCardViewModel card) => _board.ConnectAsync(card);

    public async Task ConnectEndpointAsync(DeviceEndpointViewModel ep)
    {
        // A reboot is pending: the USB transport cannot be used until Windows
        // reloads it, so refuse the connection and re-surface the prompt
        // instead of letting it half-establish.
        if (_network?.RebootRequired == true)
        {
            SetStatus("Status_RebootRequiredToConnect");
            OnRebootRequired();
            return;
        }
        await _board.ConnectEndpointAsync(ep);
    }

    public Task DisconnectAsync(DeviceCardViewModel card) => _board.DisconnectAsync(card);

    public Task DisconnectEndpointAsync(DeviceEndpointViewModel ep) => _board.DisconnectEndpointAsync(ep);

    public Task DisconnectAllAsync() => _board.DisconnectAllAsync();

    public Task SetModeAsync(DeviceCardViewModel card, ControllerMode mode) => _board.SetModeAsync(card, mode);

    public void Refresh()
    {
        if (_network == null) return;
        SetStatus("Status_Refreshing");
        _network.Refresh();
        _board.Refresh();
        SetStatus("Status_Scanning");
    }

    public async Task ConnectByIpAsync()
    {
        // Reject before a manual card is created so a refused connect leaves
        // no phantom entry behind.
        if (_network?.RebootRequired == true)
        {
            SetStatus("Status_RebootRequiredToConnect");
            OnRebootRequired();
            return;
        }

        var ip = ManualIp?.Trim();
        if (string.IsNullOrEmpty(ip) || _network == null)
        {
            SetStatus("Status_EnterValidIp");
            return;
        }

        if (!IPAddress.TryParse(ip, out var addr))
        {
            SetStatus("Status_InvalidIp");
            return;
        }

        var manual = new DiscoveredDevice { IpAddress = addr };
        var endpointId = manual.Identity.EndpointId;
        if (_board.IsConnected(endpointId))
        {
            SetStatus("Status_AlreadyConnectedTo", ip);
            return;
        }

        var ep = _board.FindEndpoint(endpointId) ?? _board.AddManualCard(manual).Endpoints[0];
        await ConnectEndpointAsync(ep);
    }

    // ----- USBip install / reboot hooks used by the shell -----------------

    /// <summary>Re-initialises the emulated-controller engine after a successful
    /// USBip install so controllers work without restarting GKME.</summary>
    public void AfterUsbipInstalled()
    {
        try { _network?.InitializeHidMaestro(); } catch { }
        SetStatus("Status_UsbipInstalled");
    }

    /// <summary>Marks a reboot as required (set by the shell after an install
    /// that needs a restart), so every later connection is refused.</summary>
    public void MarkRebootRequired() => _network?.MarkRebootRequired();

    // ----- network events -------------------------------------------------

    private void OnDeviceDiscovered(DiscoveredDevice device) => UiInvoke(() => _board.OnDeviceDiscovered(device));
    private void OnDeviceLost(DiscoveredDevice device) => UiInvoke(() => _board.OnDeviceLost(device));
    private void OnDeviceInfoUpdated(DiscoveredDevice device) => UiInvoke(() => _board.OnDeviceInfoUpdated(device));
    private void OnSessionStarted(GamepadSession session) => UiInvoke(() => _board.OnSessionStarted(session));
    private void OnSessionEnded(GamepadSession session) => UiInvoke(() => _board.OnSessionEnded(session));
    private void OnSessionConnectionLost(DeviceIdentity identity) => UiInvoke(() => _board.OnSessionConnectionLost(identity));

    private void OnRebootRequired() => UiInvoke(() => RebootRequired?.Invoke());
    private void OnUsbipInstallRequired() => UiInvoke(() => UsbipInstallRequired?.Invoke());

    /// <summary>The exception is already in the log; the status line tells the
    /// user where to find it (the log path is also in About and the menu).</summary>
    private void OnControllerError(GamepadSession session, Exception ex)
        => UiInvoke(() => SetStatus("Status_ControllerError", Log.FilePath));

    private static void UiInvoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Invoke(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
