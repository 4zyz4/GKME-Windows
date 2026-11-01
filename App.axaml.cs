using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GKME.Core;
using GKME.Localization;
using GKME.Ui;
using GKME.Web;

#pragma warning disable AVL1006

namespace GKME;

public partial class App : Application
{
    private NetworkService? _service;
    private StaticHost? _webHost;
    private Window? _mainWindow;
    private bool _showingWindow;
    private NativeMenuItem? _trayShowItem;
    private NativeMenuItem? _trayExitItem;

    /// <summary>True when the desktop accepted a tray icon. Without one the
    /// main window has to exit on close instead of hiding, otherwise the
    /// process would keep running with no way to get the window back.</summary>
    public static bool TrayAvailable { get; private set; }

    private static string IconFileName => PlatformInfo.IsWindows ? "icon.ico" : "icon.png";

    public ICommand ShowWindowCommand { get; }
    public ICommand QuitCommand { get; }

    public App()
    {
        AvaloniaXamlLoader.Load(this);
        ShowWindowCommand = new RelayCommand(() => ShowWindow());
        QuitCommand = new RelayCommand(() => _ = SafeExitAsync());
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var tcp = new TcpListener(IPAddress.Any, port);
            tcp.Start();
            tcp.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    public override void Initialize()
    {
        Resources.MergedDictionaries.Add(Localizer.Instance.BuildResourceDictionary());
        Localizer.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Resources.MergedDictionaries.Count > 0)
            Resources.MergedDictionaries[0] = Localizer.Instance.BuildResourceDictionary();
        else
            Resources.MergedDictionaries.Add(Localizer.Instance.BuildResourceDictionary());

        if (_trayShowItem != null)
            _trayShowItem.Header = Localizer.Instance.Get("Tray_ShowWindow");
        if (_trayExitItem != null)
            _trayExitItem.Header = Localizer.Instance.Get("Tray_Exit");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {

            if (IsPortInUse(NetworkService.Port))
                {
                    ShowDuplicateWarning();
                    desktop.Shutdown(-1);
                    return;
                }

            TryInitializeService();

            var vm = new MainViewModel(_service);
            StartWebServer(vm);
            var window = new MainWindow(vm);
            _mainWindow = window;
            TrySetWindowIcon(window);

            window.Show();
            window.Closed += OnMainWindowClosed;

            SetupTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Window/taskbar icon. Windows uses the multi-resolution .ico,
    /// Linux desktops need a PNG (Skia cannot decode ICO).</summary>
    private static void TrySetWindowIcon(Window window)
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, IconFileName);
            if (File.Exists(iconPath))
                window.Icon = new WindowIcon(new Bitmap(iconPath));
        }
        catch { }
    }

    private void SetupTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, IconFileName);
            var bitmap = new Bitmap(iconPath);

            _trayShowItem = new NativeMenuItem
            {
                Header = Localizer.Instance.Get("Tray_ShowWindow"),
                Command = ShowWindowCommand
            };
            _trayExitItem = new NativeMenuItem
            {
                Header = Localizer.Instance.Get("Tray_Exit"),
                Command = QuitCommand
            };

            var trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(bitmap),
                ToolTipText = "GKME - Gamepad Keyboard Mapper Engine",
                Menu = new NativeMenu
                {
                    Items =
                    {
                        _trayShowItem,
                        new NativeMenuItemSeparator(),
                        _trayExitItem
                    }
                },
                Command = ShowWindowCommand
            };

            trayIcon.Clicked += (_, _) => ShowWindow();

            var trayIcons = new TrayIcons { trayIcon };
            TrayIcon.SetIcons(this, trayIcons);
            TrayAvailable = true;
        }
        catch
        {
            // Headless sessions and desktops without a tray/StatusNotifier host
            // do not support TrayIcon. Keep running without one.
            TrayAvailable = false;
        }
    }

    private void ShowWindow()
    {
        if (_showingWindow) return;
        _showingWindow = true;
        
        if (_mainWindow != null)
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
        
        _showingWindow = false;
    }

    private void TryInitializeService()
    {
        try
        {
            _service = new NetworkService();
            _service.InitializeHidMaestro();
        }
        catch (Exception ex)
        {
            Log.Error("NetworkService initialization failed", ex);
            _service?.Dispose();
            _service = null;
        }
    }

    /// <summary>Starts the embedded HTTP host that serves GKME-Web and bridges
    /// the browser to a virtual gamepad session.</summary>
    private void StartWebServer(MainViewModel vm)
    {
        if (_service == null) return;
        try
        {
            var webHost = new StaticHost(new BrowserBridge(_service));
            _webHost = webHost;
            webHost.Start();
            if (webHost.Running)
            {
                Log.Info($"web server listening on {webHost.WebRoot} port {StaticHost.HttpPort}");
                vm.ConfigureWeb();
            }
            else
            {
                Log.Warn($"web server did not start: {webHost.LastError}");
                if (!string.IsNullOrEmpty(webHost.LastError))
                    vm.SetWebServerError(webHost.LastError);
            }
        }
        catch (Exception ex)
        {
            Log.Error("web server startup failed", ex);
            _webHost?.Dispose();
            _webHost = null;
        }
    }

    private void ShowDuplicateWarning()
    {
        try
        {
            var textBlock = new TextBlock
            {
                Text = Localizer.Instance.Get("Dialog_AlreadyRunning"),
                Margin = new Thickness(20)
            };
            
            var btn = new Button { Content = Localizer.Instance.Get("Ok"), Margin = new Thickness(10) };
            var dlg = new Window
            {
                Content = textBlock,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 300,
                Height = 150
            };
            
            var panel = new StackPanel
            {
                Children = { textBlock, btn }
            };
            
            dlg.Content = panel;
            btn.Click += (_, _) => dlg.Close();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime && lifetime.MainWindow != null)
            {
                dlg.ShowDialog(lifetime.MainWindow);
            }
            else
            {
#pragma warning disable CS8604
#pragma warning disable CS8625
                dlg.ShowDialog(null);
#pragma warning restore CS8625
#pragma warning restore CS8604
            }
        }
        catch { }
    }

    private async Task SafeExitAsync()
    {
        try { _webHost?.Dispose(); } catch { }
        _webHost = null;

        if (_mainWindow is MainWindow mw)
        {
            await mw.SafeCloseAsync();
        }
        else
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _mainWindow = null;
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}