using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CaptureFlow.App.ViewModels;
using CaptureFlow.Core.Models;

namespace CaptureFlow.App.Controls;

public partial class DocumentPreviewCanvas : UserControl
{
    private bool _isDrawing;
    private Point _drawStart;
    private Rectangle? _drawPreview;
    private readonly Dictionary<string, Rectangle> _boxRectangles = new();

    // For drag/resize
    private bool _isDragging;
    private bool _isResizing;
    private CaptureBox? _activeBox;
    private Point _dragStart;
    private NormalisedRect? _originalRect;
    private string? _resizeHandle;

    private static readonly Brush[] BoxColors =
    [
        new SolidColorBrush(Color.FromArgb(100, 37, 99, 235)),
        new SolidColorBrush(Color.FromArgb(100, 220, 38, 38)),
        new SolidColorBrush(Color.FromArgb(100, 22, 163, 74)),
        new SolidColorBrush(Color.FromArgb(100, 217, 119, 6)),
        new SolidColorBrush(Color.FromArgb(100, 147, 51, 234)),
        new SolidColorBrush(Color.FromArgb(100, 6, 182, 212)),
    ];

    private static readonly Brush[] BoxBorderColors =
    [
        new SolidColorBrush(Color.FromRgb(37, 99, 235)),
        new SolidColorBrush(Color.FromRgb(220, 38, 38)),
        new SolidColorBrush(Color.FromRgb(22, 163, 74)),
        new SolidColorBrush(Color.FromRgb(217, 119, 6)),
        new SolidColorBrush(Color.FromRgb(147, 51, 234)),
        new SolidColorBrush(Color.FromRgb(6, 182, 212)),
    ];

    private DocumentPreviewViewModel? ViewModel => DataContext as DocumentPreviewViewModel;

    public DocumentPreviewCanvas()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Keyboard shortcuts for box manipulation
        KeyDown += OnKeyDown;
        Focusable = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DocumentPreviewViewModel oldVm)
            oldVm.OverlaysChanged -= RefreshOverlays;

        if (e.NewValue is DocumentPreviewViewModel newVm)
        {
            newVm.OverlaysChanged += RefreshOverlays;
            newVm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DocumentPreviewViewModel.CurrentPageIndex))
                    RefreshOverlays();
            };
        }
    }

    private void RefreshOverlays()
    {
        OverlayCanvas.Children.Clear();
        _boxRectangles.Clear();

        var vm = ViewModel;
        if (vm?.CaptureBoxes == null || vm.Document == null) return;

        var canvasWidth = PageImage.ActualWidth;
        var canvasHeight = PageImage.ActualHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0) return;

        int colorIndex = 0;
        foreach (var box in vm.CaptureBoxes.Where(b => b.PageIndex == vm.CurrentPageIndex && b.Enabled))
        {
            var rect = CreateBoxRectangle(box, canvasWidth, canvasHeight, colorIndex);
            _boxRectangles[box.Id] = rect;
            OverlayCanvas.Children.Add(rect);

            // Add label
            var label = new TextBlock
            {
                Text = box.Name,
                FontSize = 10,
                Foreground = BoxBorderColors[colorIndex % BoxBorderColors.Length],
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                Padding = new Thickness(2, 0, 2, 0)
            };
            Canvas.SetLeft(label, box.Rect.X * canvasWidth);
            Canvas.SetTop(label, box.Rect.Y * canvasHeight - 14);
            OverlayCanvas.Children.Add(label);

            colorIndex++;
        }

        // Highlight selected
        if (vm.SelectedBox != null && _boxRectangles.TryGetValue(vm.SelectedBox.Id, out var selectedRect))
        {
            selectedRect.StrokeThickness = 3;
            selectedRect.StrokeDashArray = null;
        }
    }

    private Rectangle CreateBoxRectangle(CaptureBox box, double canvasWidth, double canvasHeight, int colorIndex)
    {
        var rect = new Rectangle
        {
            Width = box.Rect.Width * canvasWidth,
            Height = box.Rect.Height * canvasHeight,
            Fill = BoxColors[colorIndex % BoxColors.Length],
            Stroke = BoxBorderColors[colorIndex % BoxBorderColors.Length],
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection([4, 2]),
            Tag = box,
            Cursor = Cursors.SizeAll
        };

        Canvas.SetLeft(rect, box.Rect.X * canvasWidth);
        Canvas.SetTop(rect, box.Rect.Y * canvasHeight);

        rect.MouseLeftButtonDown += BoxRect_MouseDown;
        rect.MouseRightButtonDown += BoxRect_RightClick;

        return rect;
    }

    private void BoxRect_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Rectangle rect && rect.Tag is CaptureBox box)
        {
            ViewModel?.SelectBox(box);
            _isDragging = true;
            _activeBox = box;
            _dragStart = e.GetPosition(OverlayCanvas);
            _originalRect = box.Rect;
            rect.CaptureMouse();
            e.Handled = true;
        }
    }

    private void BoxRect_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Rectangle rect && rect.Tag is CaptureBox box)
        {
            ViewModel?.SelectBox(box);

            var menu = new ContextMenu();
            var deleteItem = new MenuItem { Header = "Delete Box" };
            deleteItem.Click += (_, _) => ViewModel?.DeleteSelectedBox();
            var duplicateItem = new MenuItem { Header = "Duplicate Box" };
            duplicateItem.Click += (_, _) =>
            {
                if (ViewModel != null)
                {
                    var newRect = new NormalisedRect(
                        box.Rect.X + 0.02, box.Rect.Y + 0.02,
                        box.Rect.Width, box.Rect.Height);
                    var newBox = ViewModel.AddCaptureBox(newRect);
                    newBox.Name = box.Name + " (copy)";
                    newBox.OutputHeader = box.OutputHeader + "_copy";
                    newBox.ExtractionMode = box.ExtractionMode;
                    newBox.RowTargetMode = box.RowTargetMode;
                }
            };
            menu.Items.Add(duplicateItem);
            menu.Items.Add(deleteItem);
            rect.ContextMenu = menu;
        }
    }

    private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;

        if (vm.IsDrawingMode)
        {
            _isDrawing = true;
            _drawStart = e.GetPosition(OverlayCanvas);

            _drawPreview = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection([4, 2]),
                Fill = new SolidColorBrush(Color.FromArgb(50, 37, 99, 235))
            };

            Canvas.SetLeft(_drawPreview, _drawStart.X);
            Canvas.SetTop(_drawPreview, _drawStart.Y);
            OverlayCanvas.Children.Add(_drawPreview);
            OverlayCanvas.CaptureMouse();
        }

        Focus();
    }

    private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(OverlayCanvas);

        if (_isDrawing && _drawPreview != null)
        {
            var x = Math.Min(pos.X, _drawStart.X);
            var y = Math.Min(pos.Y, _drawStart.Y);
            var w = Math.Abs(pos.X - _drawStart.X);
            var h = Math.Abs(pos.Y - _drawStart.Y);

            Canvas.SetLeft(_drawPreview, x);
            Canvas.SetTop(_drawPreview, y);
            _drawPreview.Width = w;
            _drawPreview.Height = h;
        }
        else if (_isDragging && _activeBox != null && _originalRect != null)
        {
            var canvasWidth = PageImage.ActualWidth;
            var canvasHeight = PageImage.ActualHeight;
            if (canvasWidth <= 0 || canvasHeight <= 0) return;

            var dx = (pos.X - _dragStart.X) / canvasWidth;
            var dy = (pos.Y - _dragStart.Y) / canvasHeight;

            var newRect = new NormalisedRect(
                Math.Max(0, Math.Min(1 - _originalRect.Width, _originalRect.X + dx)),
                Math.Max(0, Math.Min(1 - _originalRect.Height, _originalRect.Y + dy)),
                _originalRect.Width,
                _originalRect.Height
            );

            ViewModel?.UpdateBoxRect(_activeBox, newRect);
            RefreshOverlays();
        }

        // Update info text
        var canvasW = PageImage.ActualWidth;
        var canvasH = PageImage.ActualHeight;
        if (canvasW > 0 && canvasH > 0)
        {
            var normX = pos.X / canvasW;
            var normY = pos.Y / canvasH;
            InfoText.Text = $"Position: ({normX:F3}, {normY:F3})";
        }
    }

    private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDrawing && _drawPreview != null)
        {
            _isDrawing = false;
            OverlayCanvas.ReleaseMouseCapture();

            var canvasWidth = PageImage.ActualWidth;
            var canvasHeight = PageImage.ActualHeight;

            if (canvasWidth > 0 && canvasHeight > 0 && _drawPreview.Width > 5 && _drawPreview.Height > 5)
            {
                var x = Canvas.GetLeft(_drawPreview) / canvasWidth;
                var y = Canvas.GetTop(_drawPreview) / canvasHeight;
                var w = _drawPreview.Width / canvasWidth;
                var h = _drawPreview.Height / canvasHeight;

                var normRect = new NormalisedRect(x, y, w, h).Clamp();
                ViewModel?.AddCaptureBox(normRect);
            }

            OverlayCanvas.Children.Remove(_drawPreview);
            _drawPreview = null;
            RefreshOverlays();
        }
        else if (_isDragging)
        {
            _isDragging = false;
            _activeBox = null;
            _originalRect = null;

            // Release mouse from the rectangle
            foreach (var child in OverlayCanvas.Children.OfType<Rectangle>())
            {
                if (child.IsMouseCaptured)
                    child.ReleaseMouseCapture();
            }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm?.SelectedBox == null) return;

        double nudge = 0.005;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            nudge = 0.001;

        var box = vm.SelectedBox;
        NormalisedRect? newRect = e.Key switch
        {
            Key.Left => new NormalisedRect(box.Rect.X - nudge, box.Rect.Y, box.Rect.Width, box.Rect.Height),
            Key.Right => new NormalisedRect(box.Rect.X + nudge, box.Rect.Y, box.Rect.Width, box.Rect.Height),
            Key.Up => new NormalisedRect(box.Rect.X, box.Rect.Y - nudge, box.Rect.Width, box.Rect.Height),
            Key.Down => new NormalisedRect(box.Rect.X, box.Rect.Y + nudge, box.Rect.Width, box.Rect.Height),
            Key.Delete => null,
            _ => box.Rect
        };

        if (e.Key == Key.Delete)
        {
            vm.DeleteSelectedBox();
            RefreshOverlays();
            e.Handled = true;
            return;
        }

        if (newRect != null && newRect != box.Rect)
        {
            vm.UpdateBoxRect(box, newRect.Clamp());
            RefreshOverlays();
            e.Handled = true;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RefreshOverlays();
    }
}
