using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using GKME.Core;
using GKME.Localization;

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

        UpdateLanguageChecks();
        Localizer.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, System.EventArgs e)
    {
        UpdateLanguageChecks();
        _vm.RefreshLocalization();
    }

    private void UpdateLanguageChecks()
    {
        var selected = Localizer.Instance.SelectedLanguage;
        FollowSystemItem.IsChecked = selected == AppLanguage.FollowSystem;
        EnglishItem.IsChecked = selected == AppLanguage.English;
        SimplifiedChineseItem.IsChecked = selected == AppLanguage.SimplifiedChinese;
        TraditionalChineseHongKongItem.IsChecked = selected == AppLanguage.TraditionalChineseHongKong;
        TraditionalChineseTaiwanItem.IsChecked = selected == AppLanguage.TraditionalChineseTaiwan;
        RussianItem.IsChecked = selected == AppLanguage.Russian;
        FrenchItem.IsChecked = selected == AppLanguage.French;
    }

    private void OnLanguageMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag) return;

        switch (tag)
        {
            case nameof(AppLanguage.FollowSystem):
                Localizer.Instance.SetLanguage(AppLanguage.FollowSystem);
                break;
            case nameof(AppLanguage.English):
                Localizer.Instance.SetLanguage(AppLanguage.English);
                break;
            case nameof(AppLanguage.SimplifiedChinese):
                Localizer.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
                break;
            case nameof(AppLanguage.TraditionalChineseHongKong):
                Localizer.Instance.SetLanguage(AppLanguage.TraditionalChineseHongKong);
                break;
            case nameof(AppLanguage.TraditionalChineseTaiwan):
                Localizer.Instance.SetLanguage(AppLanguage.TraditionalChineseTaiwan);
                break;
            case nameof(AppLanguage.Russian):
                Localizer.Instance.SetLanguage(AppLanguage.Russian);
                break;
            case nameof(AppLanguage.French):
                Localizer.Instance.SetLanguage(AppLanguage.French);
                break;
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

    private async void OnMenuManualConnectClick(object? sender, RoutedEventArgs e)
    {
        await _vm.ConnectByIpAsync();
    }

    private void OnMenuRefreshClick(object? sender, RoutedEventArgs e)
    {
        _vm.Refresh();
    }

    private async void OnMenuDisconnectAllClick(object? sender, RoutedEventArgs e)
    {
        await _vm.DisconnectAllAsync();
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

    private async void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ControllerModeOption option) return;
        var card = (sender as Control)?.DataContext as DeviceCardViewModel;
        if (card == null || card.IsRestoringMode) return;
        await _vm.SetModeAsync(card, option.Mode);
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog();
        await dialog.ShowDialog(this);
    }
}