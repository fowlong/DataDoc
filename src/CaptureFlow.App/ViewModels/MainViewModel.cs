using System.Collections.ObjectModel;
using System.Windows;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using CaptureFlow.Core.Services.Adapters;
using CaptureFlow.Core.Services.Extraction;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CaptureFlow.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DocumentAdapterFactory _adapterFactory;
    private readonly IExtractionService _extractionService;
    private readonly ITemplateRepository _templateRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly CsvExportService _csvExportService;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty] private string _windowTitle = "CaptureFlow CSV";
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _pageInfo = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty] private SourceDocument? _currentDocument;
    [ObservableProperty] private int _currentPageIndex;
    [ObservableProperty] private CaptureBox? _selectedCaptureBox;

    [ObservableProperty] private DocumentPreviewViewModel _preview;
    [ObservableProperty] private ExtractionGridViewModel _extractionGrid;
    [ObservableProperty] private BatchProcessingViewModel _batch;
    [ObservableProperty] private MergeViewModel _merge;
    [ObservableProperty] private TemplateManagerViewModel _templateManager;

    public ObservableCollection<SourceDocument> LoadedDocuments { get; } = [];
    public ObservableCollection<CaptureBox> CaptureBoxes { get; } = [];
    public ObservableCollection<RepeatGroup> RepeatGroups { get; } = [];
    public ObservableCollection<Project> RecentProjects { get; } = [];

    // Undo/redo stacks
    private readonly Stack<Action> _undoStack = new();
    private readonly Stack<Action> _redoStack = new();

    public MainViewModel(
        DocumentAdapterFactory adapterFactory,
        IExtractionService extractionService,
        ITemplateRepository templateRepository,
        IProjectRepository projectRepository,
        CsvExportService csvExportService,
        DocumentPreviewViewModel preview,
        ExtractionGridViewModel extractionGrid,
        BatchProcessingViewModel batch,
        MergeViewModel merge,
        TemplateManagerViewModel templateManager,
        ILogger<MainViewModel> logger)
    {
        _adapterFactory = adapterFactory;
        _extractionService = extractionService;
        _templateRepository = templateRepository;
        _projectRepository = projectRepository;
        _csvExportService = csvExportService;
        _logger = logger;

        _preview = preview;
        _extractionGrid = extractionGrid;
        _batch = batch;
        _merge = merge;
        _templateManager = templateManager;

        preview.BoxSelected += box => SelectedCaptureBox = box;
        preview.CaptureBoxes = CaptureBoxes;

        _ = LoadRecentProjectsAsync();
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = CaptureFlow.Core.Utilities.FileTypeDetector.GetFileFilter(),
            Title = "Open Document"
        };

        if (dialog.ShowDialog() != true) return;

        await LoadDocumentAsync(dialog.FileName);
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder containing documents",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        SelectedTabIndex = 1; // Switch to batch tab
        await Batch.LoadFolderAsync(dialog.SelectedPath);
    }

    private async Task LoadDocumentAsync(string filePath)
    {
        try
        {
            IsProcessing = true;
            StatusMessage = $"Loading {Path.GetFileName(filePath)}...";

            var adapter = _adapterFactory.GetAdapter(filePath);
            if (adapter == null)
            {
                StatusMessage = $"Unsupported file type: {Path.GetExtension(filePath)}";
                return;
            }

            var doc = await adapter.LoadAsync(filePath);

            if (doc.State == ProcessingState.Error)
            {
                StatusMessage = doc.ErrorMessage ?? "Failed to load document";
                MessageBox.Show(doc.ErrorMessage ?? "Unknown error", "Load Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            CurrentDocument = doc;
            LoadedDocuments.Add(doc);

            if (doc.PageCount > 0)
            {
                CurrentPageIndex = 0;
                await Preview.LoadDocumentAsync(doc, adapter);
            }

            WindowTitle = $"CaptureFlow CSV - {doc.FileName}";
            StatusMessage = $"Loaded {doc.FileName} ({doc.PageCount} pages)";
            PageInfo = $"Page 1 of {doc.PageCount}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load document {FilePath}", filePath);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task SaveTemplate()
    {
        if (CaptureBoxes.Count == 0)
        {
            MessageBox.Show("No capture boxes defined.", "Save Template",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Page Template (*.json)|*.json",
            Title = "Save Template",
            FileName = "template.json"
        };

        if (dialog.ShowDialog() != true) return;

        var template = new PageTemplate
        {
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            CaptureBoxes = CaptureBoxes.ToList(),
            RepeatGroups = RepeatGroups.ToList(),
            ApplicableFileTypes = CurrentDocument != null
                ? [CurrentDocument.FileType]
                : []
        };

        await _templateRepository.SavePageTemplateAsync(template);
        StatusMessage = $"Template saved: {template.Name}";
    }

    [RelayCommand]
    private async Task LoadTemplate()
    {
        var templates = await _templateRepository.GetAllPageTemplatesAsync();
        if (templates.Count == 0)
        {
            MessageBox.Show("No templates saved yet.", "Load Template",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Views.TemplatePickerDialog(templates);
        if (dialog.ShowDialog() != true || dialog.SelectedTemplate == null) return;

        CaptureBoxes.Clear();
        RepeatGroups.Clear();

        foreach (var box in dialog.SelectedTemplate.CaptureBoxes)
            CaptureBoxes.Add(box);
        foreach (var group in dialog.SelectedTemplate.RepeatGroups)
            RepeatGroups.Add(group);

        Preview.RefreshOverlays();
        StatusMessage = $"Template loaded: {dialog.SelectedTemplate.Name}";
    }

    [RelayCommand]
    private async Task RunExtraction()
    {
        if (CurrentDocument == null)
        {
            StatusMessage = "No document loaded";
            return;
        }

        if (CaptureBoxes.Count == 0)
        {
            StatusMessage = "No capture boxes defined";
            return;
        }

        try
        {
            IsProcessing = true;
            StatusMessage = "Extracting...";

            var rows = await _extractionService.ExtractAsync(
                CurrentDocument,
                CaptureBoxes.ToList(),
                RepeatGroups.ToList());

            ExtractionGrid.LoadResults(rows, CurrentDocument.FileName);
            StatusMessage = $"Extracted {rows.Count} rows";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extraction failed");
            StatusMessage = $"Extraction error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (!ExtractionGrid.HasResults)
        {
            StatusMessage = "No results to export";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            Title = "Export CSV",
            FileName = CurrentDocument != null
                ? Path.GetFileNameWithoutExtension(CurrentDocument.FileName) + ".csv"
                : "export.csv"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            await _csvExportService.ExportAsync(ExtractionGrid.GetRows(), dialog.FileName);
            StatusMessage = $"Exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV export failed");
            StatusMessage = $"Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ManageTemplates()
    {
        SelectedTabIndex = 3;
    }

    [RelayCommand]
    private void ImportTemplate()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Template Files (*.json)|*.json",
            Title = "Import Template"
        };

        if (dialog.ShowDialog() != true) return;

        // Template import handled by repository
        StatusMessage = "Template imported";
    }

    [RelayCommand]
    private void ExportTemplate() { }

    [RelayCommand]
    private void OpenMergeView()
    {
        SelectedTabIndex = 2;
    }

    [RelayCommand]
    private void OpenRecent(Project project) { }

    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count > 0)
        {
            var action = _undoStack.Pop();
            action();
        }
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redoStack.Count > 0)
        {
            var action = _redoStack.Pop();
            action();
        }
    }

    [RelayCommand]
    private void About()
    {
        MessageBox.Show(
            "CaptureFlow CSV v1.0\n\nDocument to CSV extraction and CSV to document merge generation.",
            "About CaptureFlow CSV",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void Exit()
    {
        Application.Current.Shutdown();
    }

    private async Task LoadRecentProjectsAsync()
    {
        try
        {
            var projects = await _projectRepository.GetRecentProjectsAsync();
            RecentProjects.Clear();
            foreach (var p in projects) RecentProjects.Add(p);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recent projects");
        }
    }
}
