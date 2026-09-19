using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GamepadEmu.Protocol;
using GKME.Core;

namespace GKME.Ui;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly NetworkService? _network;
    private readonly DeviceModeStore _modeStore = null!;
    private string _statusText = "就绪";
    private bool _driverOk;
    private string _manualIp = "";
    private readonly Dictionary<string, GamepadSession> _sessionMap = new();
    private readonly HashSet<string> _initialConnecting = new();

    public ObservableCollection<DeviceCardViewModel> DeviceCards { get; } = new();
    public ObservableCollection<GamepadSession> Sessions { get; } = new();

    public bool HasActiveSession => Sessions.Count > 0;

    public string ManualIp
    {
        get => _manualIp;
        set { _manualIp = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public MainViewModel(NetworkService? network)
    {
        _network = network;
        _modeStore = new DeviceModeStore();
        if (_network == null)
        {
            _driverOk = false;
            StatusText = "驱动错误: 无法创建服务";
            return;
        }

        _driverOk = true;
        if (_network != null && !_network.HidMaestroAvailable)
        {
            StatusText = "HIDMaestro 驱动不可用，仅支持 DS4 和 Xbox360 模式";
        }
        _network!.SavedModeResolver = (DiscoveredDevice d) => _modeStore.Get(d.ModeKey);
        _network.OnDeviceDiscovered += OnDeviceDiscovered;
        _network.OnDeviceInfoUpdated += OnDeviceInfoUpdated;
        _network.OnSessionStarted += OnSessionStarted;
        _network.OnSessionEnded += OnSessionEnded;
        _network.OnSessionConnectionLost += OnSessionConnectionLost;
    }

    public Task<bool> InitializeAsync()
    {
        if (_driverOk)
            StartAutoScan();
        return Task.FromResult(_driverOk);
    }

    public void StartAutoScan()
    {
        if (!_driverOk || _network == null) return;
        try
        {
            StatusText = "正在扫描...";
            _network.StartDiscovery();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.NativeErrorCode == 10048)
        {
            StatusText = "GKME 已在运行中";
            Dispatcher.UIThread.Post(() => ShowAlreadyRunningDialog(), DispatcherPriority.Background);
        }
    }

    private void ShowAlreadyRunningDialog()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = MainWindow.Instance ?? (lifetime?.MainWindow as Window) ?? throw new InvalidOperationException("No main window found");

        var bgColor = Avalonia.Media.Color.Parse("#1E1E1E");
        var fgColor = Avalonia.Media.Color.Parse("#E0E0E0");
        var btnColor = Avalonia.Media.Color.Parse("#0E639C");

        var textBlock = new TextBlock
        {
            Text = "GKME 已在运行中。\n不能同时启动多个实例。",
            Margin = new Thickness(20),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = new Avalonia.Media.SolidColorBrush(fgColor),
            FontSize = 14
        };
        var btn = new Button
        {
            Content = "确定",
            Margin = new Thickness(10),
            Background = new Avalonia.Media.SolidColorBrush(btnColor),
            Foreground = Avalonia.Media.Brushes.White,
            BorderThickness = new Avalonia.Thickness(0),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(12, 6),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        var panel = new StackPanel
        {
            Children = { textBlock, btn }
        };
        var dlg = new Window
        {
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Width = 320,
            Height = 160,
            CanResize = false,
            Background = new Avalonia.Media.SolidColorBrush(bgColor)
        };
        btn.Click += (_, _) =>
        {
            dlg.Close();
            lifetime?.Shutdown();
        };
        dlg.ShowDialog(owner);
    }

    public Task ConnectAsync(DeviceCardViewModel card)
    {
        var ep = card.ActiveEndpoint;
        return ep == null ? Task.CompletedTask : ConnectEndpointAsync(ep);
    }

    public async Task ConnectEndpointAsync(DeviceEndpointViewModel ep)
    {
        if (_network == null) return;
        var device = ep.Device;
        var ip = device.IpString;
        if (_sessionMap.ContainsKey(ip)) return;

        _initialConnecting.Add(ip);
        ep.IsConnecting = true;
        StatusText = $"正在连接到 {ip}...";
        var session = await _network.ConnectAsync(device);
        if (session != null)
        {
            _sessionMap[ip] = session;
            StatusText = $"已连接: {ip}";
        }
        else
        {
            _initialConnecting.Remove(ip);
            ep.IsConnecting = false;
            StatusText = $"连接失败: {ip}";
        }
    }

    public Task DisconnectAsync(DeviceCardViewModel card)
    {
        var ep = card.ActiveEndpoint;
        return ep == null ? Task.CompletedTask : DisconnectEndpointAsync(ep);
    }

    public async Task DisconnectEndpointAsync(DeviceEndpointViewModel ep)
    {
        var ip = ep.IpAddress;
        _initialConnecting.Remove(ip);
        if (_sessionMap.TryGetValue(ip, out var session))
        {
            if (_network != null)
            {
                await _network.SendDisconnectAsync(session.Device);
            }
            else
            {
                session.Disconnect();
            }
            _sessionMap.Remove(ip);
        }
        ep.IsConnected = false;
        ep.IsConnecting = false;
        ep.IsReconnecting = false;
    }

    public async Task SetModeAsync(DeviceCardViewModel card, ControllerMode mode)
    {
        var ep = card.ActiveEndpoint;
        if (ep == null) return;
        var ip = ep.IpAddress;

        if (_sessionMap.TryGetValue(ip, out var session))
        {
            ControllerMode actualMode = await session.SetControllerModeAsync(mode);
            card.Mode = actualMode;
            _modeStore.Set(card.ModeKey, actualMode);
            if (actualMode != mode)
            {
                StatusText = "HIDMaestro 驱动不可用，已回退到 DS4 模式";
            }
        }
        else
        {
            card.Mode = mode;
            _modeStore.Set(card.ModeKey, mode);
        }
    }

    public void Refresh()
    {
        if (_network == null) return;
        StatusText = "正在刷新...";
        _network.Refresh();

        DeviceCards.Clear();
        Sessions.Clear();
        _sessionMap.Clear();
        _initialConnecting.Clear();

        foreach (var session in _network.ActiveSessions)
        {
            Sessions.Add(session);
            _sessionMap[session.Device.IpString] = session;
        }

        foreach (var device in _network.DiscoveredDevices)
        {
            AddOrUpdateCard(device);
        }

        foreach (var (ip, _) in _sessionMap)
        {
            var ep = FindEndpoint(ip);
            if (ep != null) ep.IsConnected = true;
        }

        OnPropertyChanged(nameof(HasActiveSession));
        StatusText = "正在扫描...";
    }

    public async Task ConnectByIpAsync()
    {
        var ip = ManualIp?.Trim();
        if (string.IsNullOrEmpty(ip) || _network == null)
        {
            StatusText = "请输入有效的 IP 地址";
            return;
        }

        if (!System.Net.IPAddress.TryParse(ip, out var addr))
        {
            StatusText = "IP 地址格式无效";
            return;
        }

        if (_sessionMap.ContainsKey(ip))
        {
            StatusText = $"已连接到 {ip}";
            return;
        }

        var existing = FindEndpoint(ip);
        DeviceEndpointViewModel ep;
        if (existing != null)
        {
            ep = existing;
        }
        else
        {
            var device = new DiscoveredDevice { IpAddress = addr };
            var card = new DeviceCardViewModel(device);
            ApplySavedMode(card);
            DeviceCards.Add(card);
            ep = card.Endpoints[0];
        }

        await ConnectEndpointAsync(ep);
    }

    private void ApplySavedMode(DeviceCardViewModel card)
    {
        var saved = _modeStore.Get(card.ModeKey);
        if (saved != null)
            card.Mode = saved.Value;
    }

    private DeviceCardViewModel AddOrUpdateCard(DiscoveredDevice device)
    {
        var key = device.GroupKey;
        var card = DeviceCards.FirstOrDefault(c => c.ModeKey == key);
        if (card == null)
        {
            card = new DeviceCardViewModel(device);
            ApplySavedMode(card);
            DeviceCards.Add(card);
        }
        else
        {
            card.AddOrUpdateEndpoint(device);
            if (!string.IsNullOrWhiteSpace(device.DeviceName)) card.DeviceName = device.DeviceName;
            if (!string.IsNullOrWhiteSpace(device.MacAddress)) card.MacAddress = device.MacAddress;
        }
        return card;
    }

    private DeviceEndpointViewModel? FindEndpoint(string ip)
    {
        foreach (var card in DeviceCards)
        {
            var ep = card.FindEndpoint(ip);
            if (ep != null) return ep;
        }
        return null;
    }

    private DeviceCardViewModel? FindCardByIp(string ip) =>
        DeviceCards.FirstOrDefault(c => c.FindEndpoint(ip) != null);

    private void OnDeviceDiscovered(DiscoveredDevice device)
    {
        UiInvoke(() =>
        {
            var existed = DeviceCards.Any(c => c.ModeKey == device.GroupKey && c.FindEndpoint(device.IpString) != null);
            AddOrUpdateCard(device);
            if (!existed)
                StatusText = $"发现设备: {device.IpString}";
        });
    }

    private void OnDeviceInfoUpdated(DiscoveredDevice device)
    {
        UiInvoke(() =>
        {
            var ep = FindEndpoint(device.IpString);
            if (ep == null) return;
            var card = FindCardByIp(device.IpString);
            if (card == null) return;

            if (!string.IsNullOrWhiteSpace(device.DeviceName)) card.DeviceName = device.DeviceName;
            if (!string.IsNullOrWhiteSpace(device.MacAddress)) card.MacAddress = device.MacAddress;

            // The handshake may reveal a MAC that another (broadcast) card already
            // owns. Merge the manual card into the discovered one so a device is
            // never listed twice.
            var mac = device.MacAddress;
            if (!string.IsNullOrWhiteSpace(mac))
            {
                var other = DeviceCards.FirstOrDefault(c => c != card && c.ModeKey == mac);
                if (other != null)
                {
                    MergeCards(other, card);
                }
            }
        });
    }

    /// <summary>Move every endpoint of <paramref name="remove"/> into
    /// <paramref name="keep"/> and drop the now-empty card.</summary>
    private static void MergeCards(DeviceCardViewModel keep, DeviceCardViewModel remove)
    {
        foreach (var ep in remove.Endpoints.ToList())
        {
            var device = ep.Device;
            remove.RemoveEndpoint(ep);
            var kep = keep.AddOrUpdateEndpoint(device);
            if (!string.IsNullOrWhiteSpace(device.DeviceName)) keep.DeviceName = device.DeviceName;
            if (!string.IsNullOrWhiteSpace(device.MacAddress)) keep.MacAddress = device.MacAddress;
            kep.IsConnected = ep.IsConnected;
            kep.IsConnecting = ep.IsConnecting;
            kep.IsReconnecting = ep.IsReconnecting;
        }
    }

    private void OnSessionStarted(GamepadSession session)
    {
        UiInvoke(() =>
        {
            var ip = session.Device.IpString;
            _initialConnecting.Remove(ip);
            if (!Sessions.Contains(session))
            {
                Sessions.Add(session);
            }
            _sessionMap[ip] = session;
            OnPropertyChanged(nameof(HasActiveSession));

            var card = AddOrUpdateCard(session.Device);
            if (!string.IsNullOrWhiteSpace(session.Device.DeviceName)) card.DeviceName = session.Device.DeviceName;
            if (!string.IsNullOrWhiteSpace(session.Device.MacAddress)) card.MacAddress = session.Device.MacAddress;

            var ep = card.FindEndpoint(ip);
            if (ep != null)
            {
                ep.IsConnected = true;
                ep.IsConnecting = false;
                ep.IsReconnecting = false;
            }
            card.SetModeRestored(session.Mode);
        });
    }

    private void OnSessionEnded(GamepadSession session)
    {
        UiInvoke(() =>
        {
            Sessions.Remove(session);
            OnPropertyChanged(nameof(HasActiveSession));
            var ip = session.Device.IpString;
            _initialConnecting.Remove(ip);
            _sessionMap.Remove(ip);

            var card = FindCardByIp(ip);
            var ep = card?.FindEndpoint(ip);
            if (ep != null)
            {
                ep.IsConnected = false;
                ep.IsConnecting = false;
                ep.IsReconnecting = false;
            }
            if (card != null)
            {
                card.SetModeRestored(ControllerMode.Ds4);
            }
            StatusText = $"已断开: {ip}";
        });
    }

    private void OnSessionConnectionLost(string ip)
    {
        UiInvoke(() =>
        {
            var card = FindCardByIp(ip);
            var ep = card?.FindEndpoint(ip);
            if (ep != null)
            {
                ep.IsConnected = false;
                ep.IsReconnecting = true;
            }
            StatusText = $"连接丢失: {ip}";
        });
    }

    private static void UiInvoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Invoke(action);
    }

    public async Task ShutdownAsync()
    {
        if (_network == null) return;
        _network.OnDeviceDiscovered -= OnDeviceDiscovered;
        _network.OnDeviceInfoUpdated -= OnDeviceInfoUpdated;
        _network.OnSessionStarted -= OnSessionStarted;
        _network.OnSessionEnded -= OnSessionEnded;
        _network.OnSessionConnectionLost -= OnSessionConnectionLost;
        _network.StopDiscovery();
        await _network.DisconnectAllAsync();
        _initialConnecting.Clear();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
