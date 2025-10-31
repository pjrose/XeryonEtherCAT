using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using XeryonEtherCAT.App.ViewModels;

namespace XeryonEtherCAT.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.DisposeAsync();
        }
    }
}
