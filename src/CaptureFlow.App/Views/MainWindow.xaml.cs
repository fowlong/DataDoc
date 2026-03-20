using System.Windows;
using System.Windows.Controls;
using CaptureFlow.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CaptureFlow.App.Views;

public partial class MainWindow : Window
{
    private UIElement[]? _tabPanels;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
        Loaded += (_, _) => UpdateTabVisibility(0);
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tc)
            UpdateTabVisibility(tc.SelectedIndex);
    }

    private void UpdateTabVisibility(int selectedIndex)
    {
        _tabPanels ??= [ExtractTabContent, CreateTabContent, TemplatesTabContent];
        for (int i = 0; i < _tabPanels.Length; i++)
            _tabPanels[i].Visibility = i == selectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }
}
