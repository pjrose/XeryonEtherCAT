using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace XeryonEtherCAT.App.Views;

public partial class AxisControl : UserControl
{
    public AxisControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
