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

    public async Task ConnectAsync(DeviceCardViewModel card)
    {
        if (_network == null) return;
        var device = card.Device;
        var ip = device.IpString;
        _initialConnecting.Add(ip);
        card.IsConnecting = true;
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
            card.IsConnecting = false;
            StatusText = $"连接失败: {ip}";
        }
    }

    public async Task DisconnectAsync(DeviceCardViewModel card)
    {
        var ip = card.IpAddress;
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
        card.IsConnected = false;
        card.IsConnecting = false;
        card.IsReconnecting = false;
    }

    public async Task SetModeAsync(DeviceCardViewModel card, ControllerMode mode)
    {
        var ip = card.IpAddress;

        if (_sessionMap.TryGetValue(ip, out var session))
        {
            ControllerMode actualMode = await session.SetControllerModeAsync(mode);
            card.Mode = actualMode;
            _modeStore.Set(card.Device.ModeKey, actualMode);
            if (actualMode != mode)
            {
                StatusText = "HIDMaestro 驱动不可用，已回退到 DS4 模式";
            }
        }
        else
        {
            card.Mode = mode;
            _modeStore.Set(card.Device.ModeKey, mode);
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
            DeviceCards.Add(CreateCard(session.Device));
        }

        foreach (var device in _network.DiscoveredDevices)
        {
            if (!_sessionMap.ContainsKey(device.IpString))
            {
                DeviceCards.Add(CreateCard(device));
            }
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

        var device = new DiscoveredDevice
        {
            DeviceName = ip,
            IpAddress = addr,
        };

        var existing = DeviceCards.FirstOrDefault(c => c.IpAddress == ip);
        DeviceCardViewModel card;
        if (existing != null)
        {
            card = existing;
        }
        else
        {
            card = CreateCard(device);
            DeviceCards.Add(card);
        }

        _initialConnecting.Add(ip);
        card.IsConnecting = true;
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
            card.IsConnecting = false;
            StatusText = $"连接失败: {ip}";
        }
    }

    private DeviceCardViewModel CreateCard(DiscoveredDevice device)
    {
        var card = new DeviceCardViewModel(device);
        var saved = _modeStore.Get(device.ModeKey);
        if (saved != null)
            card.Mode = saved.Value;
        return card;
    }

    private void OnDeviceDiscovered(DiscoveredDevice device)
    {
        UiInvoke(() =>
        {
            var ip = device.IpString;
            if (!DeviceCards.Any(c => c.IpAddress == ip))
            {
                DeviceCards.Add(CreateCard(device));
                StatusText = $"发现设备: {device.IpString}";
            }
        });
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
            var card = DeviceCards.FirstOrDefault(c => c.IpAddress == ip);
            if (card != null)
            {
                card.IsConnected = true;
                card.IsConnecting = false;
                card.IsReconnecting = false;
                card.SetModeRestored(session.Mode);
            }
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
            var card = DeviceCards.FirstOrDefault(c => c.IpAddress == ip);
            if (card != null)
            {
                card.IsConnected = false;
                card.IsConnecting = false;
                card.IsReconnecting = false;
                card.SetModeRestored(ControllerMode.Ds4);
            }
            StatusText = $"已断开: {ip}";
        });
    }

    private void OnSessionConnectionLost(string ip)
    {
        UiInvoke(() =>
        {
            var card = DeviceCards.FirstOrDefault(c => c.IpAddress == ip);
            if (card != null)
            {
                card.IsConnected = false;
                card.IsReconnecting = true;
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