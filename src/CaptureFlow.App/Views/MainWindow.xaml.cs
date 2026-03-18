using System.Windows;
using CaptureFlow.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CaptureFlow.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }
}
