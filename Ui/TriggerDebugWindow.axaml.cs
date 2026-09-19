using Avalonia.Controls;

namespace GKME.Ui;

public partial class TriggerDebugWindow : Window
{
    public TriggerDebugWindow()
    {
        InitializeComponent();
    }

    public TriggerDebugWindow(TriggerDebugViewModel vm) : this()
    {
        DataContext = vm;
    }
}
