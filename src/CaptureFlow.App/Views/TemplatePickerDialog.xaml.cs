using System.Windows;
using System.Windows.Input;
using CaptureFlow.Core.Models;

namespace CaptureFlow.App.Views;

public partial class TemplatePickerDialog : Window
{
    public PageTemplate? SelectedTemplate { get; private set; }

    public TemplatePickerDialog(IReadOnlyList<PageTemplate> templates)
    {
        InitializeComponent();
        TemplateList.ItemsSource = templates;
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        SelectedTemplate = TemplateList.SelectedItem as PageTemplate;
        if (SelectedTemplate != null)
            DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TemplateList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        SelectedTemplate = TemplateList.SelectedItem as PageTemplate;
        if (SelectedTemplate != null)
            DialogResult = true;
    }
}
