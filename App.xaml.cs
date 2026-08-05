using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinApplication = System.Windows.Application;
using WinFormsApplication = System.Windows.Forms.Application;
using WinFormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WinFormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using WinFormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WinFormsMouseEventArgs = System.Windows.Forms.MouseEventArgs;
using WinFormsMouseButtons = System.Windows.Forms.MouseButtons;
using WinFormsIcon = System.Drawing.Icon;
using GamepadEmuHost.Core;
using GamepadEmuHost.Ui;

namespace GamepadEmuHost;

public partial class App : WinApplication
{
    private const string VigemInstallerName = "ViGEmBus_1.22.0_x64_x86_arm64.exe";

    public NetworkService? Service { get; private set; }

    private WinFormsNotifyIcon? _notifyIcon;
    private Window? _mainWindow;

    public void SetMainWindow(Window window)
    {
        _mainWindow = window;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!NetworkService.IsVigemBusInstalled())
        {
            if (!PromptInstallVigemBus())
            {
                Shutdown();
                return;
            }
        }

        try
        {
            Service = new NetworkService();
            if (!Service.InitializeHidMaestro())
            {
                Service.Dispose();
                Service = null;
            }
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
            : WinFormsIcon.ExtractAssociatedIcon(Path.Combine(appDir, "GamepadEmuHost.exe"));

        _notifyIcon = new WinFormsNotifyIcon
        {
            Icon = resolvedIcon ?? WinFormsIcon.ExtractAssociatedIcon(Environment.SystemDirectory + @"\shell32.dll"),
            Text = "Gamepad Emu Host",
            Visible = true
        };

        var menu = new WinFormsContextMenuStrip();
        var showItem = new WinFormsToolStripMenuItem("显示窗口");
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

    private static bool PromptInstallVigemBus()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var installerPath = Path.Combine(appDir, VigemInstallerName);
        if (!File.Exists(installerPath))
            installerPath = Path.Combine(appDir, "Install", "VigemDrv", VigemInstallerName);

        var window = new Window
        {
            Title = "ViGEmBus 驱动",
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.SingleBorderWindow,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = Brushes.White,
            Topmost = true,
            ShowInTaskbar = true
        };

        var stack = new StackPanel { Margin = new Thickness(20) };

        stack.Children.Add(new TextBlock
        {
            Text = "未检测到 ViGEmBus 驱动",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var descText = "游戏手柄仿真需要 ViGEmBus 驱动才能正常工作。\n" +
                       (File.Exists(installerPath) ? "是否立即安装？" : "请下载 ViGEmBus 驱动后手动安装。");

        stack.Children.Add(new TextBlock
        {
            Text = descText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20)
        });

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (File.Exists(installerPath))
        {
            var installBtn = new Button
            {
                Content = "安装",
                Width = 80,
                Height = 30,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            installBtn.MouseEnter += (_, _) => installBtn.Background = new SolidColorBrush(Color.FromRgb(0x11, 0x77, 0xBB));
            installBtn.MouseLeave += (_, _) => installBtn.Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));
            installBtn.Click += (_, _) =>
            {
                window.Hide();
                var ui = WinApplication.Current.Dispatcher;

                new Thread(() =>
                {
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = installerPath,
                            UseShellExecute = true,
                            Verb = "runas"
                        };
                        using var proc = Process.Start(startInfo);
                        proc?.WaitForExit();
                    }
                    catch (Exception ex)
                    {
                        ui.Invoke(() =>
                            MessageBox.Show($"启动安装程序失败：{ex.Message}\n路径：{installerPath}",
                                "错误", MessageBoxButton.OK, MessageBoxImage.Error));
                        return;
                    }

                    ui.Invoke(() => Current.Shutdown());
                })
                { IsBackground = true }.Start();
            };
            btnPanel.Children.Add(installBtn);
        }

        var exitBtn = new Button
        {
            Content = "退出",
            Width = 80,
            Height = 30,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        exitBtn.MouseEnter += (_, _) => exitBtn.Background = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        exitBtn.MouseLeave += (_, _) => exitBtn.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        exitBtn.Click += (_, _) =>
        {
            window.DialogResult = false;
            window.Close();
        };

        btnPanel.Children.Add(exitBtn);
        stack.Children.Add(btnPanel);
        window.Content = stack;

        window.ShowDialog();
        return window.DialogResult == true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _notifyIcon?.Visible = false;
            _notifyIcon?.Dispose();
            Service?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}