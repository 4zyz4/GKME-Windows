using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GKME.Core;
using GKME.Localization;

namespace GKME.Ui;

public partial class AboutDialog : Window
{
    private const string GkmeSourceUrl = "https://github.com/4zyz4/gkme";

    private static readonly IBrush PrimaryText = new SolidColorBrush(Color.Parse("#E0E0E0"));
    private static readonly IBrush SecondaryText = new SolidColorBrush(Color.Parse("#9D9D9D"));
    private static readonly IBrush DividerBrush = new SolidColorBrush(Color.Parse("#3E3E3E"));

    /// <summary>关于页展示的一条开源项目。项目名保持英文原名，
    /// 用途来自本地化键，点击后打开对应网址。</summary>
    private sealed record AboutProject(string Name, string DescriptionKey, string Url);

    /// <summary>README「鸣谢 → 参考与衍生项目」。</summary>
    private static readonly AboutProject[] ReferencedProjects =
    {
        new("HIDMaestro", "About_ProjectHidMaestroDesc", "https://github.com/hifihedgehog/HIDMaestro"),
        new("usbip-win2", "About_ProjectUsbipDesc", "https://github.com/vadimgrn/usbip-win2"),
        new("Moonlight", "About_ProjectMoonlightDesc", "https://github.com/moonlight-stream/moonlight-android"),
        new("Axixi2233/moonlight-android", "About_ProjectAxixiDesc", "https://github.com/Axixi2233/moonlight-android"),
        new("qiin2333/moonlight-vplus", "About_ProjectVPlusDesc", "https://github.com/qiin2333/moonlight-vplus"),
    };

    /// <summary>README「鸣谢 → 使用的开源库」。</summary>
    private static readonly AboutProject[] OpenSourceLibraries =
    {
        new("SDL3 / HIDAPI", "About_ProjectSdl3Desc", "https://libsdl.org/"),
        new("Dagger Hilt", "About_ProjectHiltDesc", "https://dagger.dev/hilt/"),
        new("Shizuku", "About_ProjectShizukuDesc", "https://shizuku.rikka.app/"),
    };

    public AboutDialog()
    {
        InitializeComponent();
        BuildProjectLists();
        UpdateDynamicText();

        Localizer.Instance.LanguageChanged += OnLanguageChanged;
        Closed += OnClosed;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateDynamicText();
        BuildProjectLists();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Localizer.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void UpdateDynamicText()
    {
        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString()
            ?? Localizer.Instance.Get("UnknownVersion");
        VersionTextBlock.Text = Localizer.Instance.Format("Version", version);
    }

    /// <summary>用代码生成开源项目行，避免在 XAML 里重复八份相同结构。</summary>
    private void BuildProjectLists()
    {
        ReferencedProjectsPanel.Children.Clear();
        OpenSourceLibrariesPanel.Children.Clear();
        AddProjectRows(ReferencedProjectsPanel, ReferencedProjects);
        AddProjectRows(OpenSourceLibrariesPanel, OpenSourceLibraries);
    }

    private void AddProjectRows(StackPanel panel, IReadOnlyList<AboutProject> projects)
    {
        for (var i = 0; i < projects.Count; i++)
        {
            if (i > 0)
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Background = DividerBrush,
                    Margin = new Thickness(12, 0, 12, 0),
                });
            }

            panel.Children.Add(CreateProjectRow(projects[i]));
        }
    }

    private Control CreateProjectRow(AboutProject project)
    {
        var name = new TextBlock
        {
            Text = project.Name,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = PrimaryText,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var arrow = new TextBlock
        {
            Text = "↗",
            FontSize = 13,
            Foreground = SecondaryText,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(name);
        Grid.SetColumn(arrow, 1);
        titleRow.Children.Add(arrow);

        var description = new TextBlock
        {
            Text = Localizer.Instance.Get(project.DescriptionKey),
            FontSize = 12,
            Foreground = SecondaryText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var row = new Button
        {
            Classes = { "project" },
            Content = new StackPanel { Children = { titleRow, description } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        row.Click += (_, _) => OpenExternalUrl(project.Url);
        return row;
    }

    private void OnSourceClick(object? sender, RoutedEventArgs e) => OpenExternalUrl(GkmeSourceUrl);

    private void OnLicenseClick(object? sender, RoutedEventArgs e)
    {
        _ = new LicenseDialog().ShowDialog(this);
    }

    private void OnSponsorClick(object? sender, RoutedEventArgs e)
    {
        _ = new SponsorDialog().ShowDialog(this);
    }

    /// <summary>打开外部链接；没有可用浏览器时给出提示，而不是崩溃。</summary>
    private void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"about: could not open {url}: {ex.Message}");
            _ = ShowMessageAsync(Localizer.Instance.Get("About_UnableToOpenLink"));
        }
    }

    private async Task ShowMessageAsync(string message)
    {
        var closeButton = new Button
        {
            Content = Localizer.Instance.Get("Close"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 6, 16, 6),
            Background = new SolidColorBrush(Color.Parse("#3A3A3A")),
            Foreground = PrimaryText,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
        };

        var dialog = new Window
        {
            Title = Localizer.Instance.Get("About_Title"),
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        FontSize = 13,
                        Foreground = PrimaryText,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    closeButton,
                },
            },
        };

        closeButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }
}
