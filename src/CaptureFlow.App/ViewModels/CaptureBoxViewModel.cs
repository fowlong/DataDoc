using CaptureFlow.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CaptureFlow.App.ViewModels;

public partial class CaptureBoxViewModel : ObservableObject
{
    [ObservableProperty] private CaptureBox? _box;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _outputHeader = "";
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private ExtractionMode _extractionMode = ExtractionMode.NativeWithOcrFallback;
    [ObservableProperty] private RowTargetMode _rowTargetMode = RowTargetMode.DocumentRow;
    [ObservableProperty] private string _previewValue = "";
    [ObservableProperty] private bool _enabled = true;

    public void LoadBox(CaptureBox box)
    {
        Box = box;
        Name = box.Name;
        OutputHeader = box.OutputHeader;
        PageIndex = box.PageIndex;
        ExtractionMode = box.ExtractionMode;
        RowTargetMode = box.RowTargetMode;
        Enabled = box.Enabled;
    }

    partial void OnNameChanged(string value)
    {
        if (Box != null) Box.Name = value;
    }

    partial void OnOutputHeaderChanged(string value)
    {
        if (Box != null) Box.OutputHeader = value;
    }

    partial void OnExtractionModeChanged(ExtractionMode value)
    {
        if (Box != null) Box.ExtractionMode = value;
    }

    partial void OnRowTargetModeChanged(RowTargetMode value)
    {
        if (Box != null) Box.RowTargetMode = value;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (Box != null) Box.Enabled = value;
    }
}
