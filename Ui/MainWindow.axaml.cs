using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GKME.Core;
using GKME.Localization;
using GKMD.Internal.Usbip;

namespace GKME.Ui;

public partial class MainWindow : Window
{
    private static Window? _instance;
    private readonly MainViewModel _vm;
    private int _rebootPromptShown;
    private int _usbipInstallPromptShown;

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

        // Dialogs live with the shell, not the view model; the view model only
        // raises the request.
        _vm.RebootRequired += OnRebootRequired;
        _vm.UsbipInstallRequired += OnUsbipInstallRequired;
        _vm.AlreadyRunning += OnAlreadyRunning;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        Title = $"GKME {version}";

        UpdateLanguageChecks();
        Localizer.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnRebootRequired()
    {
        if (Interlocked.Exchange(ref _rebootPromptShown, 1) != 0) return;
        Dispatcher.UIThread.Post(ShowRebootRequiredDialog, DispatcherPriority.Background);
    }

    private void OnUsbipInstallRequired()
    {
        if (Interlocked.Exchange(ref _usbipInstallPromptShown, 1) != 0) return;
        Dispatcher.UIThread.Post(ShowUsbipInstallDialog, DispatcherPriority.Background);
    }

    private void OnAlreadyRunning() =>
        Dispatcher.UIThread.Post(ShowAlreadyRunningDialog, DispatcherPriority.Background);

    private void ShowUsbipInstallDialog()
    {
        var owner = ResolveOwner();

        bool applied = false;
        var dlg = new UsbipInstallDialog();
        dlg.Closed += (_, _) =>
        {
            // If nothing was installed (the user cancelled), allow the prompt
            // again on a later controller attempt.
            if (!applied) Interlocked.Exchange(ref _usbipInstallPromptShown, 0);
        };
        dlg.InstallRequested += async () =>
        {
            dlg.SetBusy();
            dlg.SetStatus(Localizer.Instance.Get("Status_UsbipExtracting"));

            UsbipInstallResult result;
            try
            {
                result = await Task.Run(() => UsbipDriverInstaller.Install(
                    s => Dispatcher.UIThread.Post(() => dlg.SetStatus(s)),
                    interactive: true));
            }
            catch (Exception ex)
            {
                dlg.SetCompleted(Localizer.Instance.Format("Status_InstallFailed", ex.Message));
                return;
            }

            if (result == UsbipInstallResult.Success)
            {
                applied = true;
                // Refresh availability so controllers can be created without
                // restarting GKME.
                _vm.AfterUsbipInstalled();
                dlg.Close();
            }
            else if (result == UsbipInstallResult.RebootRequired)
            {
                applied = true;
                // Close the install dialog first, then raise a fresh reboot
                // prompt. Mark the service so every later connection is
                // refused until the machine restarts.
                dlg.Close();
                _vm.MarkRebootRequired();
                OnRebootRequired();
            }
            else
            {
                dlg.SetCompleted(Localizer.Instance.Get("Status_InstallIncomplete"));
            }
        };

        if (owner != null)
            _ = dlg.ShowDialog(owner);
        else
            dlg.Show();
    }

    private void ShowRebootRequiredDialog()
    {
        _vm.SetStatus("Status_RebootForDriver");
        var owner = ResolveOwner();

        var dlg = new RebootPromptDialog();
        dlg.Closed += (_, _) => Interlocked.Exchange(ref _rebootPromptShown, 0);
        if (owner != null)
            _ = dlg.ShowDialog(owner);
        else
            dlg.Show();
    }

    private void ShowAlreadyRunningDialog()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = ResolveOwner() ?? throw new InvalidOperationException("No main window found");

        var bgColor = Avalonia.Media.Color.Parse("#1E1E1E");
        var fgColor = Avalonia.Media.Color.Parse("#E0E0E0");
        var btnColor = Avalonia.Media.Color.Parse("#0E639C");

        var textBlock = new TextBlock
        {
            Text = Localizer.Instance.Get("Dialog_AlreadyRunning"),
            Margin = new Thickness(20),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = new Avalonia.Media.SolidColorBrush(fgColor),
            FontSize = 14
        };
        var btn = new Button
        {
            Content = Localizer.Instance.Get("Ok"),
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

    private static Window? ResolveOwner()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return Instance ?? (lifetime?.MainWindow as Window);
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
        if (App.TrayAvailable && PlatformInfo.IsWindows)
        {
            Hide();
            return;
        }

        // Windows keeps running in the tray. Linux exits instead: a desktop
        // without a StatusNotifier host accepts the tray registration but never
        // shows the icon, and a hidden window would then be unreachable.
        _ = SafeCloseAsync();
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