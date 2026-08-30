using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using GKME.Core;
using GKME.Ui;

namespace GKME;

public partial class App : Application
{
    private NetworkService? _service;
    private Window? _mainWindow;

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
            var window = new MainWindow(vm);
            _mainWindow = window;
            
            window.Show();
            window.Closed += OnMainWindowClosed;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TryInitializeService()
    {
        try
        {
            _service = new NetworkService();
            _service.InitializeHidMaestro();
        }
        catch
        {
            _service?.Dispose();
            _service = null;
        }
    }

    private void ShowDuplicateWarning()
    {
        try
        {
            var textBlock = new TextBlock
            {
                Text = "GKME 已在运行中。\n不能同时启动多个实例。",
                Margin = new Thickness(20)
            };
            
            var btn = new Button { Content = "确定", Margin = new Thickness(10) };
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
                dlg.ShowDialog(null);
            }
        }
        catch { }
    }

    private async Task SafeExitAsync()
    {
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