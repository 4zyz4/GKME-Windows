using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinApplication = System.Windows.Application;
using WinFormsApplication = System.Windows.Forms.Application;
using WinFormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WinFormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using WinFormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WinFormsMouseEventArgs = System.Windows.Forms.MouseEventArgs;
using WinFormsMouseButtons = System.Windows.Forms.MouseButtons;
using WinFormsIcon = System.Drawing.Icon;
using GKME.Core;
using GKME.Ui;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]

namespace GKME;

public partial class App : WinApplication
{
    private bool _crashing;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_crashing) return;
        ExceptionLogger.Record(e.Exception, $"Dispatcher: {e.Exception.Source ?? "Unknown"}");
        ShowCrashDialog(e.Exception);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (_crashing) return;
        var ex = e.ExceptionObject as Exception;
        if (ex != null)
            ExceptionLogger.Record(ex, "AppDomain.UnhandledException");

        ShowCrashDialog(ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (_crashing) return;
        ExceptionLogger.Record(e.Exception, "TaskScheduler.UnobservedTaskException");
        ShowCrashDialog(e.Exception);
    }

    private void ShowCrashDialog(Exception? ex)
    {
        _crashing = true;
        try
        {
            var text = ExceptionLogger.FormatForClipboard();
            var dialog = new ExceptionDialog(text);
            dialog.Owner = _mainWindow;
            dialog.ShowDialog();
        }
        catch
        {
            try
            {
                var msg = ex != null
                    ? $"异常：{ex.GetType().Name}\n{ex.Message}\n\n{ex.StackTrace}"
                    : "发生未知异常";
                MessageBox.Show(msg, "程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }
        finally
        {
            try
            {
                Shutdown(-1);
            }
            catch { }
        }
    }

    public NetworkService? Service { get; private set; }

    private WinFormsNotifyIcon? _notifyIcon;
    private Window? _mainWindow;

    public void SetMainWindow(Window window)
    {
        _mainWindow = window;
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var tcp = new TcpListener(System.Net.IPAddress.Any, port);
            tcp.Start();
            tcp.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (IsPortInUse(NetworkService.Port))
        {
            MessageBox.Show(
                "GKME 已在运行中。\n不能同时启动多个实例。",
                "GKME",
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation);
            Shutdown(-1);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        try
        {
            Service = new NetworkService();
            Service.InitializeHidMaestro();
        }
        catch
        {
            Service?.Dispose();
            Service = null;
        }

        var vm = new MainViewModel(Service);
        var window = new MainWindow(vm);
        _mainWindow = window;
        window.Show();

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var iconPath = Path.Combine(appDir, "icon.ico");
        var resolvedIcon = iconPath != null && System.IO.File.Exists(iconPath)
            ? new WinFormsIcon(iconPath)
            : WinFormsIcon.ExtractAssociatedIcon(Path.Combine(appDir, "GKME.exe"));

        _notifyIcon = new WinFormsNotifyIcon
        {
            Icon = resolvedIcon ?? WinFormsIcon.ExtractAssociatedIcon(Environment.SystemDirectory + @"\shell32.dll"),
            Text = "GKME",
            Visible = true
        };

        var menu = new WinFormsContextMenuStrip();
        var showItem = new WinFormsToolStripMenuItem("显示窗口");
        var exceptionItem = new WinFormsToolStripMenuItem("异常日志");
        exceptionItem.Click += (_, _) =>
        {
            try
            {
                if (_mainWindow != null)
                {
                    var dialog = new ExceptionDialog();
                    dialog.Owner = _mainWindow;
                    dialog.ShowDialog();
                }
            }
            catch { }
        };

        var exitItem = new WinFormsToolStripMenuItem("退出");
        exitItem.Click += async (_, _) =>
        {
            var mainWindow = _mainWindow as MainWindow;
            if (mainWindow != null)
                await mainWindow.SafeCloseAsync();
            else
                WinApplication.Current.Shutdown();
        };
        showItem.Click += (_, _) =>
        {
            if (_mainWindow != null)
            {
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
            }
        };
        menu.Items.Add(showItem);
        menu.Items.Add(exceptionItem);
        menu.Items.Add(exitItem);
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinFormsMouseButtons.Left)
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _notifyIcon?.Visible = false;
            _notifyIcon?.Dispose();
        }
        catch { }
        base.OnExit(e);
        // Service.Dispose() 会调用 ViGEm / HIDMaestro 的原生清理，可能被驱动调用
        // 阻塞挂起；放后台线程并限时等待，超时则放弃，确保 Environment.Exit(0)
        // 必然执行——否则进程会在托盘退出后残留。
        try
        {
            if (Service != null)
                Task.Run(Service.Dispose).Wait(TimeSpan.FromSeconds(3));
        }
        catch { }
        Environment.Exit(0);
    }
}