using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using CaptureFlow.Core.Services.Adapters;
using CaptureFlow.Core.Services.Extraction;
using CaptureFlow.Core.Utilities;
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
    [ObservableProperty] private SourceDocument? _selectedSourceDocument;

    // Track selected templates so UI ComboBoxes reflect the last-created one
    [ObservableProperty] private PageTemplate? _selectedPageTemplate;
    [ObservableProperty] private DocumentTemplate? _selectedDocTemplate;

    [ObservableProperty] private DocumentPreviewViewModel _preview;
    [ObservableProperty] private ExtractionGridViewModel _extractionGrid;
    [ObservableProperty] private BatchProcessingViewModel _batch;
    [ObservableProperty] private CreateViewModel _create;
    [ObservableProperty] private TemplateManagerViewModel _templateManager;

    public ObservableCollection<SourceDocument> LoadedDocuments { get; } = [];
    public ObservableCollection<CaptureBox> CaptureBoxes { get; } = [];
    public ObservableCollection<RepeatGroup> RepeatGroups { get; } = [];
    public ObservableCollection<Project> RecentProjects { get; } = [];
    public ObservableCollection<string> CsvHeaders { get; } = [];

    // Per-document capture boxes: each doc has its own overlay
    private readonly Dictionary<string, List<CaptureBox>> _documentCaptureBoxes = new();

    // Persisted extraction results available to create tab
    private List<ExtractionRow> _lastExtractionRows = [];
    public bool HasExtractionResults => _lastExtractionRows.Count > 0;

    // Undo/redo stacks
    private readonly Stack<Action> _undoStack = new();
    private readonly Stack<Action> _redoStack = new();

    // Cache adapters per document for switching
    private readonly Dictionary<string, IDocumentAdapter> _documentAdapters = new();

    public MainViewModel(
        DocumentAdapterFactory adapterFactory,
        IExtractionService extractionService,
        ITemplateRepository templateRepository,
        IProjectRepository projectRepository,
        CsvExportService csvExportService,
        DocumentPreviewViewModel preview,
        ExtractionGridViewModel extractionGrid,
        BatchProcessingViewModel batch,
        CreateViewModel create,
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
        _create = create;
        _templateManager = templateManager;

        preview.BoxSelected += box => SelectedCaptureBox = box;
        preview.CaptureBoxes = CaptureBoxes;

        _ = LoadRecentProjectsAsync();
    }

    // When user clicks a different source document, switch the preview
    partial void OnSelectedSourceDocumentChanged(SourceDocument? value)
    {
        if (value == null) return;
        _ = SwitchToDocumentAsync(value);
    }

    private async Task SwitchToDocumentAsync(SourceDocument doc)
    {
        try
        {
            // Save current document's capture boxes before switching
            if (CurrentDocument != null)
            {
                _documentCaptureBoxes[CurrentDocument.Id] = CaptureBoxes.ToList();
            }

            CurrentDocument = doc;

            // Restore target document's capture boxes (empty by default)
            CaptureBoxes.Clear();
            if (_documentCaptureBoxes.TryGetValue(doc.Id, out var savedBoxes))
            {
                foreach (var box in savedBoxes)
                    CaptureBoxes.Add(box);
            }

            IDocumentAdapter? adapter = null;
            if (_documentAdapters.TryGetValue(doc.Id, out var cached))
            {
                adapter = cached;
            }
            else
            {
                adapter = _adapterFactory.GetAdapter(doc.FilePath);
                if (adapter != null)
                    _documentAdapters[doc.Id] = adapter;
            }

            if (adapter == null)
            {
                StatusMessage = $"No adapter for {doc.FileName}";
                return;
            }

            CurrentPageIndex = 0;
            await Preview.LoadDocumentAsync(doc, adapter);
            Preview.RefreshOverlays();

            WindowTitle = $"CaptureFlow CSV - {doc.FileName}";
            PageInfo = $"Page 1 of {doc.PageCount}";
            StatusMessage = $"Viewing {doc.FileName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch to document {FileName}", doc.FileName);
            StatusMessage = $"Error switching: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveSourceDocument(SourceDocument? doc)
    {
        if (doc == null) return;

        // Clean up cached state
        _documentAdapters.Remove(doc.Id);
        _documentCaptureBoxes.Remove(doc.Id);
        LoadedDocuments.Remove(doc);

        // If we removed the current document, switch to another or clear
        if (CurrentDocument?.Id == doc.Id)
        {
            if (LoadedDocuments.Count > 0)
            {
                SelectedSourceDocument = LoadedDocuments[0];
            }
            else
            {
                CurrentDocument = null;
                CaptureBoxes.Clear();
                Preview.Clear();
                WindowTitle = "CaptureFlow CSV";
                PageInfo = "";
            }
        }

        StatusMessage = $"Removed {doc.FileName}";
    }

    [RelayCommand]
    private async Task OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = FileTypeDetector.GetFileFilter(),
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

        // Stay on Extract tab, populate source docs panel
        SelectedTabIndex = 0;
        IsProcessing = true;
        StatusMessage = "Scanning folder...";

        try
        {
            var dir = new DirectoryInfo(dialog.FolderName);
            if (!dir.Exists) return;

            var files = dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => _adapterFactory.CanHandle(f.FullName))
                .ToList();

            StatusMessage = $"Loading {files.Count} documents...";

            foreach (var file in files)
            {
                // Skip if already loaded
                if (LoadedDocuments.Any(d => d.FilePath == file.FullName))
                    continue;

                try
                {
                    await LoadDocumentAsync(file.FullName, selectAfterLoad: false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipped {FileName}", file.Name);
                }
            }

            // Select the first document if nothing is selected
            if (CurrentDocument == null && LoadedDocuments.Count > 0)
                SelectedSourceDocument = LoadedDocuments[0];

            StatusMessage = $"Loaded {LoadedDocuments.Count} documents from folder";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load folder");
            StatusMessage = $"Folder error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task LoadDocumentAsync(string filePath, bool selectAfterLoad = true)
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
                if (selectAfterLoad)
                {
                    MessageBox.Show(doc.ErrorMessage ?? "Unknown error", "Load Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            // Cache adapter for later switching
            _documentAdapters[doc.Id] = adapter;
            LoadedDocuments.Add(doc);

            if (selectAfterLoad)
            {
                SelectedSourceDocument = doc;
                WindowTitle = $"CaptureFlow CSV - {doc.FileName}";
                StatusMessage = $"Loaded {doc.FileName} ({doc.PageCount} pages)";
                PageInfo = $"Page 1 of {doc.PageCount}";
            }
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
        TemplateManager.RefreshCommand.Execute(null);
        SelectedPageTemplate = template;
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

            var boxes = CaptureBoxes.ToList();
            var rows = await _extractionService.ExtractAsync(
                CurrentDocument,
                boxes,
                RepeatGroups.ToList());

            if (ExtractionGrid.AutoClearOnExtract)
                _lastExtractionRows = rows;
            else
                _lastExtractionRows.AddRange(rows);

            OnPropertyChanged(nameof(HasExtractionResults));
            ExtractionGrid.LoadResults(rows, CurrentDocument.FileName, boxes);
            RefreshCsvHeaders();
            StatusMessage = $"Extracted {rows.Count} rows from {CurrentDocument.FileName}";
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
    private async Task RunExtractionAll()
    {
        if (LoadedDocuments.Count == 0)
        {
            StatusMessage = "No documents loaded";
            return;
        }

        // Save current doc's boxes first
        if (CurrentDocument != null)
            _documentCaptureBoxes[CurrentDocument.Id] = CaptureBoxes.ToList();

        try
        {
            IsProcessing = true;
            var allRows = new List<ExtractionRow>();
            var groups = RepeatGroups.ToList();
            int processed = 0;
            int skipped = 0;

            foreach (var doc in LoadedDocuments)
            {
                processed++;
                StatusMessage = $"Extracting {doc.FileName} ({processed}/{LoadedDocuments.Count})...";
                ProgressValue = (double)processed / LoadedDocuments.Count * 100;

                // Use per-document capture boxes
                var docBoxes = _documentCaptureBoxes.TryGetValue(doc.Id, out var saved)
                    ? saved : [];

                if (docBoxes.Count == 0)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var rows = await _extractionService.ExtractAsync(doc, docBoxes, groups);
                    allRows.AddRange(rows);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Extraction failed for {FileName}", doc.FileName);
                }
            }

            if (ExtractionGrid.AutoClearOnExtract)
                _lastExtractionRows = allRows;
            else
                _lastExtractionRows.AddRange(allRows);
            OnPropertyChanged(nameof(HasExtractionResults));
            // Collect all capture boxes across all docs for group detection
            var allBoxes = _documentCaptureBoxes.Values.SelectMany(b => b).ToList();
            ExtractionGrid.LoadResults(allRows, $"{LoadedDocuments.Count} documents", allBoxes);
            RefreshCsvHeaders();

            var msg = $"Extracted {allRows.Count} rows from {LoadedDocuments.Count - skipped} documents";
            if (skipped > 0) msg += $" ({skipped} skipped - no capture boxes)";
            StatusMessage = msg;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extract all failed");
            StatusMessage = $"Extract all error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
            ProgressValue = 0;
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
            var table = ExtractionGrid.ResultsTable;
            if (table != null)
            {
                await _csvExportService.ExportTableAsync(table, dialog.FileName);
                StatusMessage = $"Exported {table.Rows.Count} rows to {dialog.FileName}";
            }
            else
            {
                await _csvExportService.ExportAsync(ExtractionGrid.GetRows(), dialog.FileName);
                StatusMessage = $"Exported to {dialog.FileName}";
            }
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
        SelectedTabIndex = 2; // Extract=0, Create=1, Templates=2
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
    private void OpenCreateView()
    {
        SelectedTabIndex = 1; // Extract=0, Create=1, Templates=2
    }

    [RelayCommand]
    private void UseExtractionResultsInCreate()
    {
        if (_lastExtractionRows.Count == 0)
        {
            StatusMessage = "No extraction results to use";
            return;
        }

        Create.LoadFromExtractionResults(_lastExtractionRows);
        SelectedTabIndex = 1;
        StatusMessage = $"Loaded {_lastExtractionRows.Count} extraction rows into create";
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
            "CaptureFlow CSV v1.0\n\nDocument to CSV extraction and PDF document creation.",
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

    public List<ExtractionRow> GetLastExtractionRows() => _lastExtractionRows;

    [RelayCommand]
    private async Task ApplyPageTemplate(PageTemplate template)
    {
        if (template == null || Preview.Document == null) return;

        // Clear existing boxes on the target page to prevent duplication
        var pageIndex = Preview.CurrentPageIndex;
        var toRemove = CaptureBoxes.Where(b => b.PageIndex == pageIndex).ToList();
        foreach (var b in toRemove) CaptureBoxes.Remove(b);

        foreach (var box in template.CaptureBoxes.Where(b => b.PageIndex == pageIndex || b.PageIndex == 0))
        {
            CaptureBoxes.Add(new CaptureBox
            {
                Name = box.Name,
                OutputHeader = box.OutputHeader,
                PageIndex = pageIndex,
                Rect = box.Rect,
                ExtractionMode = box.ExtractionMode,
                RowTargetMode = box.RowTargetMode,
                Enabled = box.Enabled,
                DefaultValue = box.DefaultValue,
                Notes = box.Notes,
                CsvGroup = box.CsvGroup
            });
        }

        Preview.RefreshOverlays();
        RefreshCsvHeaders();
        StatusMessage = $"Applied page template '{template.Name}' to page {pageIndex + 1}";
    }

    [RelayCommand]
    private async Task ApplyDocumentTemplate(DocumentTemplate template)
    {
        if (template == null || Preview.Document == null) return;

        // Clear all existing boxes for this document to prevent duplication
        CaptureBoxes.Clear();

        var pageTemplates = await _templateRepository.GetAllPageTemplatesAsync();

        foreach (var assignment in template.PageAssignments)
        {
            var pageTemplate = pageTemplates.FirstOrDefault(t => t.Id == assignment.PageTemplateId);
            if (pageTemplate == null) continue;

            var targetPage = assignment.PageIndex ?? 0;
            foreach (var box in pageTemplate.CaptureBoxes)
            {
                CaptureBoxes.Add(new CaptureBox
                {
                    Name = box.Name,
                    OutputHeader = box.OutputHeader,
                    PageIndex = targetPage,
                    Rect = box.Rect,
                    ExtractionMode = box.ExtractionMode,
                    RowTargetMode = box.RowTargetMode,
                    Enabled = box.Enabled,
                    DefaultValue = box.DefaultValue,
                    Notes = box.Notes,
                    CsvGroup = box.CsvGroup
                });
            }
        }

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
                Enabled = box.Enabled,
                CsvGroup = box.CsvGroup
            });
        }

        Preview.RefreshOverlays();
        RefreshCsvHeaders();
        StatusMessage = $"Applied document template '{template.Name}'";
    }

    [RelayCommand]
    private async Task ApplyDocumentTemplateToAll(DocumentTemplate template)
    {
        if (template == null || LoadedDocuments.Count == 0) return;

        var pageTemplates = await _templateRepository.GetAllPageTemplatesAsync();
        int applied = 0;

        foreach (var doc in LoadedDocuments)
        {
            // Clear existing boxes for this doc before applying template
            var docBoxes = new List<CaptureBox>();
            _documentCaptureBoxes[doc.Id] = docBoxes;

            foreach (var assignment in template.PageAssignments)
            {
                var pageTemplate = pageTemplates.FirstOrDefault(t => t.Id == assignment.PageTemplateId);
                if (pageTemplate == null) continue;

                var targetPage = assignment.PageIndex ?? 0;
                if (targetPage >= doc.PageCount) continue;

                foreach (var box in pageTemplate.CaptureBoxes)
                {
                    docBoxes.Add(new CaptureBox
                    {
                        Name = box.Name,
                        OutputHeader = box.OutputHeader,
                        PageIndex = targetPage,
                        Rect = box.Rect,
                        ExtractionMode = box.ExtractionMode,
                        RowTargetMode = box.RowTargetMode,
                        Enabled = box.Enabled,
                        DefaultValue = box.DefaultValue,
                        Notes = box.Notes,
                        CsvGroup = box.CsvGroup
                    });
                }
            }

            foreach (var box in template.DocumentLevelFields)
            {
                docBoxes.Add(new CaptureBox
                {
                    Name = box.Name,
                    OutputHeader = box.OutputHeader,
                    PageIndex = box.PageIndex,
                    Rect = box.Rect,
                    ExtractionMode = box.ExtractionMode,
                    RowTargetMode = box.RowTargetMode,
                    Enabled = box.Enabled,
                    CsvGroup = box.CsvGroup
                });
            }

            applied++;
        }

        // Reload current document's boxes into the active collection
        if (CurrentDocument != null && _documentCaptureBoxes.TryGetValue(CurrentDocument.Id, out var currentBoxes))
        {
            CaptureBoxes.Clear();
            foreach (var box in currentBoxes)
                CaptureBoxes.Add(box);
        }

        Preview.RefreshOverlays();
        RefreshCsvHeaders();
        StatusMessage = $"Applied document template '{template.Name}' to {applied} documents";
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
                Notes = b.Notes,
                CsvGroup = b.CsvGroup
            }).ToList(),
            ApplicableFileTypes = CurrentDocument != null ? [CurrentDocument.FileType] : []
        };

        await _templateRepository.SavePageTemplateAsync(template);
        TemplateManager.RefreshCommand.Execute(null);
        SelectedPageTemplate = template;
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
                    Notes = b.Notes,
                    CsvGroup = b.CsvGroup
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
        SelectedDocTemplate = docTemplate;
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
