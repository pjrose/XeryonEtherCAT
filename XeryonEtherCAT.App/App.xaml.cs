using System.Windows;
using XeryonEtherCAT.App.ViewModels;

namespace XeryonEtherCAT.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;

    public App()
    {
        InitializeComponent();
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _mainWindow = new MainWindow
        {
            DataContext = new MainWindowViewModel(Dispatcher)
        };
        _mainWindow.Show();
    }
}
