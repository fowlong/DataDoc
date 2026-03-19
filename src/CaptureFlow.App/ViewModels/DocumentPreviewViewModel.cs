using System.Collections.ObjectModel;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.App.ViewModels;

public partial class DocumentPreviewViewModel : ObservableObject
{
    private readonly ILogger<DocumentPreviewViewModel> _logger;
    private IDocumentAdapter? _currentAdapter;

    [ObservableProperty] private SourceDocument? _document;
    [ObservableProperty] private int _currentPageIndex;
    [ObservableProperty] private byte[]? _currentPageImage;
    [ObservableProperty] private double _zoomLevel = 1.0;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _pageLabel = "";
    [ObservableProperty] private bool _isDrawingMode;
    [ObservableProperty] private CaptureBox? _selectedBox;
    [ObservableProperty] private double _pageWidth;
    [ObservableProperty] private double _pageHeight;

    public ObservableCollection<CaptureBox>? CaptureBoxes { get; set; }

    public event Action<CaptureBox>? BoxSelected;
    public event Action? OverlaysChanged;

    public DocumentPreviewViewModel(ILogger<DocumentPreviewViewModel> logger)
    {
        _logger = logger;
    }

    public async Task LoadDocumentAsync(SourceDocument document, IDocumentAdapter adapter)
    {
        Document = document;
        _currentAdapter = adapter;
        CurrentPageIndex = 0;
        await RenderCurrentPageAsync();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        if (Document != null)
        {
            PageLabel = $"Page {value + 1} of {Document.PageCount}";
            _ = RenderCurrentPageAsync();
        }
    }

    private async Task RenderCurrentPageAsync()
    {
        if (Document == null || _currentAdapter == null) return;
        if (CurrentPageIndex < 0 || CurrentPageIndex >= Document.PageCount) return;

        try
        {
            IsLoading = true;

            var page = Document.Pages[CurrentPageIndex];
            PageWidth = page.OriginalWidth;
            PageHeight = page.OriginalHeight;

            // Render at reasonable resolution
            var renderWidth = Math.Max(800, (int)(page.OriginalWidth * 2));
            var imageBytes = await _currentAdapter.RenderPageAsync(Document, CurrentPageIndex, renderWidth);

            if (imageBytes.Length > 0)
            {
                CurrentPageImage = imageBytes;
            }
            else if (page.PreviewImagePng != null)
            {
                CurrentPageImage = page.PreviewImagePng;
            }

            PageLabel = $"Page {CurrentPageIndex + 1} of {Document.PageCount}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render page {PageIndex}", CurrentPageIndex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CurrentPageIndex > 0)
            CurrentPageIndex--;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (Document != null && CurrentPageIndex < Document.PageCount - 1)
            CurrentPageIndex++;
    }

    [RelayCommand]
    private void ZoomIn()
    {
        ZoomLevel = Math.Min(4.0, ZoomLevel * 1.25);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        ZoomLevel = Math.Max(0.25, ZoomLevel / 1.25);
    }

    [RelayCommand]
    private void ZoomFit()
    {
        ZoomLevel = 1.0;
    }

    [RelayCommand]
    private void ToggleDrawingMode()
    {
        IsDrawingMode = !IsDrawingMode;
    }

    public CaptureBox AddCaptureBox(NormalisedRect rect)
    {
        var box = new CaptureBox
        {
            Name = $"Field {(CaptureBoxes?.Count ?? 0) + 1}",
            OutputHeader = $"Column{(CaptureBoxes?.Count ?? 0) + 1}",
            PageIndex = CurrentPageIndex,
            Rect = rect
        };

        CaptureBoxes?.Add(box);
        SelectedBox = box;
        BoxSelected?.Invoke(box);
        OverlaysChanged?.Invoke();
        return box;
    }

    public void SelectBox(CaptureBox box)
    {
        SelectedBox = box;
        BoxSelected?.Invoke(box);
    }

    public void DeleteSelectedBox()
    {
        if (SelectedBox != null && CaptureBoxes != null)
        {
            CaptureBoxes.Remove(SelectedBox);
            SelectedBox = null;
            OverlaysChanged?.Invoke();
        }
    }

    public void Clear()
    {
        Document = null;
        _currentAdapter = null;
        CurrentPageImage = null;
        SelectedBox = null;
        PageLabel = "";
        OverlaysChanged?.Invoke();
    }

    public void RefreshOverlays()
    {
        OverlaysChanged?.Invoke();
    }

    public void UpdateBoxRect(CaptureBox box, NormalisedRect newRect)
    {
        box.Rect = newRect;
        OverlaysChanged?.Invoke();
    }
}
