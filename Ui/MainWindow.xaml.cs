using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GamepadEmuHost.Core;

namespace GamepadEmuHost.Ui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;
        _vm = vm;
        InitializeComponent();
        this.Closing += OnClosing;
        this.StateChanged += OnStateChanged;
        Loaded += async (_, _) => await _vm.InitializeAsync();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            if (Application.Current is App app)
                app.SetMainWindow(this);
        }
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
        Application.Current.Shutdown();
        try
        {
            Closing -= OnClosing;
        }
        catch { }
    }

    private async void OnManualConnectClick(object sender, RoutedEventArgs e)
    {
        await _vm.ConnectByIpAsync();
    }

    private async void OnCardConnectClick(object sender, RoutedEventArgs e)
    {
        var card = (sender as FrameworkElement)?.DataContext as DeviceCardViewModel;
        if (card != null)
            await _vm.ConnectAsync(card);
    }

    private async void OnCardDisconnectClick(object sender, RoutedEventArgs e)
    {
        var card = (sender as FrameworkElement)?.DataContext as DeviceCardViewModel;
        if (card != null)
            await _vm.DisconnectAsync(card);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _vm.Refresh();
    }

    private async void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ControllerMode mode) return;
        var card = (sender as FrameworkElement)?.DataContext as DeviceCardViewModel;
        if (card == null || card.IsRestoringMode) return;
        await _vm.SetModeAsync(card, mode);
    }
}