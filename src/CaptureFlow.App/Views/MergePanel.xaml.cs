using System.Windows.Controls;
using System.Windows.Input;
using CaptureFlow.App.ViewModels;

namespace CaptureFlow.App.Views;

public partial class MergePanel : UserControl
{
    public MergePanel()
    {
        InitializeComponent();
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MergeViewModel vm || !vm.IsAnnotationMode)
            return;

        var image = sender as System.Windows.Controls.Image;
        if (image == null) return;

        var position = e.GetPosition(image);
        double normX = position.X / image.ActualWidth;
        double normY = position.Y / image.ActualHeight;

        normX = Math.Clamp(normX, 0, 1);
        normY = Math.Clamp(normY, 0, 1);

        vm.AddAnnotationAtPosition(normX, normY);
    }
}
