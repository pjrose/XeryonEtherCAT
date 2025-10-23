using System.Windows;

namespace XeryonEtherCAT.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var service = new XeryonEtherCAT.Ethercat.XeryonEthercatService();
        var viewModel = new ViewModels.MainWindowViewModel(service);

        var window = new MainWindow
        {
            DataContext = viewModel,
            Background = (System.Windows.Media.Brush)Resources["WindowBackgroundBrush"],
            Foreground = (System.Windows.Media.Brush)Resources["PrimaryTextBrush"],
        };

        window.Loaded += async (_, _) => await viewModel.InitializeAsync();
        MainWindow = window;
        window.Show();
    }
}
