using CommunityToolkit.Mvvm.ComponentModel;

namespace CaptureFlow.Core.Models;

/// <summary>
/// Represents a text annotation placed on a merge preview that gets applied to exported documents.
/// Coordinates are normalized 0-1 with top-left origin.
/// </summary>
public partial class MergeAnnotation : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private double _normX;
    [ObservableProperty] private double _normY;
    [ObservableProperty] private double _fontSize = 12;
    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private bool _isSelected;
}
