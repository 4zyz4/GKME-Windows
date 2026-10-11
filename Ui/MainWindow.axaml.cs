using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using GKME.Core;

namespace GKME.Ui;

public partial class MainWindow : Window
{
    private static Window? _instance;
    private readonly MainViewModel _vm;

    public static Window? Instance => _instance;

    public MainWindow() => _vm = null!;

    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;
        _vm = vm;
        InitializeComponent();
        _instance = this;
        this.Closing += OnClosing;
        this.Loaded += async (_, _) => await _vm.InitializeAsync();
        
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        Title = $"GKME {version}";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    public async Task SafeCloseAsync()
    {
        try
        {
            await _vm.ShutdownAsync();
        }
        catch { }
        try
        {
            Closing -= OnClosing;
        }
        catch { }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }

    private async void OnManualConnectClick(object? sender, RoutedEventArgs e)
    {
        await _vm.ConnectByIpAsync();
    }

    private async void OnCardConnectClick(object? sender, RoutedEventArgs e)
    {
        var card = (sender as Control)?.DataContext as DeviceCardViewModel;
        if (card != null)
            await _vm.ConnectAsync(card);
    }

    private async void OnCardDisconnectClick(object? sender, RoutedEventArgs e)
    {
        var card = (sender as Control)?.DataContext as DeviceCardViewModel;
        if (card != null)
            await _vm.DisconnectAsync(card);
    }

    private async void OnEndpointConnectClick(object? sender, RoutedEventArgs e)
    {
        var ep = (sender as Control)?.DataContext as DeviceEndpointViewModel;
        if (ep != null)
            await _vm.ConnectEndpointAsync(ep);
    }

    private async void OnEndpointDisconnectClick(object? sender, RoutedEventArgs e)
    {
        var ep = (sender as Control)?.DataContext as DeviceEndpointViewModel;
        if (ep != null)
            await _vm.DisconnectEndpointAsync(ep);
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        _vm.Refresh();
    }

    private void OnWebClick(object? sender, RoutedEventArgs e)
    {
        _vm.OpenWeb();
    }

    private async void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ControllerMode mode) return;
        var card = (sender as Control)?.DataContext as DeviceCardViewModel;
        if (card == null || card.IsRestoringMode) return;
        await _vm.SetModeAsync(card, mode);
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog();
        await dialog.ShowDialog(this);
    }
}