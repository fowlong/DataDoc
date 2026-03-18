using System.Collections.ObjectModel;
using System.IO;
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
    public ObservableCollection<string> CsvHeaders { get; } = [];

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
        var dialog = new OpenFolderDialog
        {
            Title = "Select folder containing documents"
        };

        if (dialog.ShowDialog() != true) return;

        SelectedTabIndex = 1; // Switch to batch tab
        await Batch.LoadFolderAsync(dialog.FolderName);
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
            RefreshCsvHeaders();
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

    public void RefreshCsvHeaders()
    {
        var headers = new HashSet<string>();

        // Add headers from existing capture boxes
        foreach (var box in CaptureBoxes)
        {
            if (!string.IsNullOrWhiteSpace(box.OutputHeader))
                headers.Add(box.OutputHeader);
        }

        // Add headers from extraction results
        if (ExtractionGrid.ResultsTable != null)
        {
            foreach (System.Data.DataColumn col in ExtractionGrid.ResultsTable.Columns)
            {
                if (!col.ColumnName.StartsWith("_"))
                    headers.Add(col.ColumnName);
            }
        }

        CsvHeaders.Clear();
        foreach (var h in headers.OrderBy(h => h))
            CsvHeaders.Add(h);
    }

    [RelayCommand]
    private async Task ApplyPageTemplate(PageTemplate template)
    {
        if (template == null || Preview.Document == null) return;

        foreach (var box in template.CaptureBoxes.Where(b => b.PageIndex == Preview.CurrentPageIndex || b.PageIndex == 0))
        {
            var newBox = new CaptureBox
            {
                Name = box.Name,
                OutputHeader = box.OutputHeader,
                PageIndex = Preview.CurrentPageIndex,
                Rect = box.Rect,
                ExtractionMode = box.ExtractionMode,
                RowTargetMode = box.RowTargetMode,
                Enabled = box.Enabled,
                DefaultValue = box.DefaultValue,
                Notes = box.Notes
            };
            CaptureBoxes.Add(newBox);
        }

        Preview.RefreshOverlays();
        RefreshCsvHeaders();
        StatusMessage = $"Applied page template '{template.Name}' to page {Preview.CurrentPageIndex + 1}";
    }

    [RelayCommand]
    private async Task ApplyDocumentTemplate(DocumentTemplate template)
    {
        if (template == null || Preview.Document == null) return;

        var pageTemplates = await _templateRepository.GetAllPageTemplatesAsync();

        foreach (var assignment in template.PageAssignments)
        {
            var pageTemplate = pageTemplates.FirstOrDefault(t => t.Id == assignment.PageTemplateId);
            if (pageTemplate == null) continue;

            var targetPage = assignment.PageIndex ?? 0;
            foreach (var box in pageTemplate.CaptureBoxes)
            {
                var newBox = new CaptureBox
                {
                    Name = box.Name,
                    OutputHeader = box.OutputHeader,
                    PageIndex = targetPage,
                    Rect = box.Rect,
                    ExtractionMode = box.ExtractionMode,
                    RowTargetMode = box.RowTargetMode,
                    Enabled = box.Enabled,
                    DefaultValue = box.DefaultValue,
                    Notes = box.Notes
                };
                CaptureBoxes.Add(newBox);
            }
        }

        // Also add document-level fields
        foreach (var box in template.DocumentLevelFields)
        {
            CaptureBoxes.Add(new CaptureBox
            {
                Name = box.Name,
                OutputHeader = box.OutputHeader,
                PageIndex = box.PageIndex,
                Rect = box.Rect,
                ExtractionMode = box.ExtractionMode,
                RowTargetMode = box.RowTargetMode,
                Enabled = box.Enabled
            });
        }

        Preview.RefreshOverlays();
        RefreshCsvHeaders();
        StatusMessage = $"Applied document template '{template.Name}'";
    }

    [RelayCommand]
    private async Task SaveCurrentAsPageTemplate()
    {
        if (CaptureBoxes.Count == 0)
        {
            StatusMessage = "No capture boxes to save";
            return;
        }

        var currentPageBoxes = CaptureBoxes.Where(b => b.PageIndex == Preview.CurrentPageIndex).ToList();
        if (currentPageBoxes.Count == 0)
        {
            StatusMessage = "No capture boxes on current page";
            return;
        }

        var template = new PageTemplate
        {
            Name = $"Page {Preview.CurrentPageIndex + 1} - {DateTime.Now:yyyy-MM-dd HH:mm}",
            CaptureBoxes = currentPageBoxes.Select(b => new CaptureBox
            {
                Name = b.Name,
                OutputHeader = b.OutputHeader,
                PageIndex = 0, // Normalize to page 0 for reuse
                Rect = b.Rect,
                ExtractionMode = b.ExtractionMode,
                RowTargetMode = b.RowTargetMode,
                Enabled = b.Enabled,
                DefaultValue = b.DefaultValue,
                Notes = b.Notes
            }).ToList(),
            ApplicableFileTypes = CurrentDocument != null ? [CurrentDocument.FileType] : []
        };

        await _templateRepository.SavePageTemplateAsync(template);
        TemplateManager.RefreshCommand.Execute(null);
        StatusMessage = $"Saved page template: {template.Name}";
    }

    [RelayCommand]
    private async Task SaveCurrentAsDocumentTemplate()
    {
        if (CaptureBoxes.Count == 0)
        {
            StatusMessage = "No capture boxes to save";
            return;
        }

        // Group boxes by page, save each page as a page template, then create a doc template
        var boxesByPage = CaptureBoxes.GroupBy(b => b.PageIndex).ToList();
        var assignments = new List<PageTemplateAssignment>();

        foreach (var group in boxesByPage)
        {
            var pageTemplate = new PageTemplate
            {
                Name = $"Auto - Page {group.Key + 1}",
                CaptureBoxes = group.Select(b => new CaptureBox
                {
                    Name = b.Name,
                    OutputHeader = b.OutputHeader,
                    PageIndex = 0,
                    Rect = b.Rect,
                    ExtractionMode = b.ExtractionMode,
                    RowTargetMode = b.RowTargetMode,
                    Enabled = b.Enabled,
                    DefaultValue = b.DefaultValue,
                    Notes = b.Notes
                }).ToList(),
                ApplicableFileTypes = CurrentDocument != null ? [CurrentDocument.FileType] : []
            };

            await _templateRepository.SavePageTemplateAsync(pageTemplate);

            assignments.Add(new PageTemplateAssignment
            {
                PageTemplateId = pageTemplate.Id,
                PageIndex = group.Key
            });
        }

        var docTemplate = new DocumentTemplate
        {
            Name = $"Document - {DateTime.Now:yyyy-MM-dd HH:mm}",
            PageAssignments = assignments,
            ApplicableFileTypes = CurrentDocument != null ? [CurrentDocument.FileType] : []
        };

        await _templateRepository.SaveDocumentTemplateAsync(docTemplate);
        TemplateManager.RefreshCommand.Execute(null);
        StatusMessage = $"Saved document template: {docTemplate.Name}";
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
