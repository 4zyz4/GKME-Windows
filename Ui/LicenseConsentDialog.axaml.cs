using Avalonia.Controls;

namespace GKME.Ui;

/// <summary>首次启动（仅限没有安装程序的 Linux 端）要求用户阅读并同意
/// GPLv3。同意状态由 <see cref="Core.SettingsStore"/> 持久化，之后不再询问。</summary>
public partial class LicenseConsentDialog : Window
{
    /// <summary>True only when the user checked the box and clicked
    /// “Agree and continue”; closing the window any other way means decline.</summary>
    public bool Accepted { get; private set; }

    public LicenseConsentDialog()
    {
        InitializeComponent();
        LicenseText.Text = GplLicenseText.Load();

        AcceptCheck.IsCheckedChanged += (_, _) => AcceptButton.IsEnabled = AcceptCheck.IsChecked == true;
        DeclineButton.Click += (_, _) => Close();
        AcceptButton.Click += (_, _) =>
        {
            Accepted = true;
            Close();
        };
    }
}
