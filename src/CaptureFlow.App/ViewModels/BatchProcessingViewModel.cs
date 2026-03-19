using System.Collections.ObjectModel;
using System.IO;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using CaptureFlow.Core.Services.Extraction;
using CaptureFlow.Core.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.App.ViewModels;

public partial class BatchProcessingViewModel : ObservableObject
{
    private readonly IBatchProcessor _batchProcessor;
    private readonly ITemplateRepository _templateRepository;
    private readonly CsvExportService _csvExportService;
    private readonly ILogger<BatchProcessingViewModel> _logger;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _folderPath = "";
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private int _processedFiles;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private DocumentTemplate? _selectedTemplate;
    [ObservableProperty] private System.Data.DataTable? _resultsTable;

    public ObservableCollection<BatchFileItem> Files { get; } = [];
    public ObservableCollection<DocumentTemplate> AvailableTemplates { get; } = [];
    public ObservableCollection<BatchError> Errors { get; } = [];

    private List<ExtractionRow> _extractedRows = [];

    // File type filters
    [ObservableProperty] private bool _includePdf = true;
    [ObservableProperty] private bool _includeDocx = true;
    [ObservableProperty] private bool _includeImages;
    [ObservableProperty] private bool _includeEmail;
    [ObservableProperty] private bool _includeText;

    public BatchProcessingViewModel(
        IBatchProcessor batchProcessor,
        ITemplateRepository templateRepository,
        CsvExportService csvExportService,
        ILogger<BatchProcessingViewModel> logger)
    {
        _batchProcessor = batchProcessor;
        _templateRepository = templateRepository;
        _csvExportService = csvExportService;
        _logger = logger;
    }

    public async Task LoadFolderAsync(string folderPath)
    {
        FolderPath = folderPath;
        Files.Clear();

        var dir = new DirectoryInfo(folderPath);
        if (!dir.Exists) return;

        foreach (var file in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            var fileType = FileTypeDetector.Detect(file.FullName);
            if (fileType == SupportedFileType.Unknown) continue;
            if (!ShouldInclude(fileType)) continue;

            Files.Add(new BatchFileItem
            {
                FilePath = file.FullName,
                FileName = file.Name,
                FileType = fileType,
                Status = "Pending"
            });
        }

        TotalFiles = Files.Count;
        StatusText = $"{TotalFiles} files found";

        // Load available templates
        var templates = await _templateRepository.GetAllDocumentTemplatesAsync();
        AvailableTemplates.Clear();
        foreach (var t in templates) AvailableTemplates.Add(t);
    }

    private bool ShouldInclude(SupportedFileType type) => type switch
    {
        SupportedFileType.Pdf => IncludePdf,
        SupportedFileType.Docx => IncludeDocx,
        SupportedFileType.Png or SupportedFileType.Jpg or SupportedFileType.Tiff or SupportedFileType.Bmp => IncludeImages,
        SupportedFileType.Eml or SupportedFileType.Msg => IncludeEmail,
        SupportedFileType.Txt or SupportedFileType.Html or SupportedFileType.Rtf => IncludeText,
        _ => false
    };

    [RelayCommand]
    private async Task RunBatch()
    {
        if (SelectedTemplate == null || Files.Count == 0) return;

        _cts = new CancellationTokenSource();
        IsProcessing = true;
        ProcessedFiles = 0;
        ErrorCount = 0;
        Errors.Clear();

        var progress = new Progress<BatchProgress>(p =>
        {
            ProcessedFiles = p.ProcessedFiles;
            ErrorCount = p.ErrorCount;
            Progress = TotalFiles > 0 ? (double)p.ProcessedFiles / TotalFiles * 100 : 0;
            StatusText = p.CurrentStatus ?? $"Processing {p.CurrentFile}...";

            foreach (var file in Files)
            {
                if (file.FilePath == p.CurrentFile)
                    file.Status = "Processing";
            }

            foreach (var err in p.Errors.Where(e => !Errors.Any(existing => existing.FilePath == e.FilePath)))
                Errors.Add(err);
        });

        try
        {
            var pageTemplates = await _templateRepository.GetAllPageTemplatesAsync();
            var filePaths = Files.Select(f => f.FilePath).ToList();

            _extractedRows = await _batchProcessor.ProcessBatchAsync(
                filePaths, SelectedTemplate, pageTemplates.ToList(), progress, _cts.Token);

            LoadResultsTable();
            StatusText = $"Batch complete: {_extractedRows.Count} rows extracted from {ProcessedFiles} files ({ErrorCount} errors)";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Batch cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch processing failed");
            StatusText = $"Batch failed: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void CancelBatch()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task ExportResults()
    {
        if (_extractedRows.Count == 0) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = "batch_export.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            await _csvExportService.ExportAsync(_extractedRows, dialog.FileName);
            StatusText = $"Exported to {dialog.FileName}";
        }
    }

    private void LoadResultsTable()
    {
        var table = new System.Data.DataTable();
        var headers = _extractedRows.SelectMany(r => r.Cells.Keys).Distinct().ToList();

        table.Columns.Add("_SourceFile", typeof(string));
        table.Columns.Add("_Page", typeof(string));
        foreach (var h in headers)
            table.Columns.Add(h, typeof(string));

        foreach (var row in _extractedRows)
        {
            var dr = table.NewRow();
            dr["_SourceFile"] = row.SourceFileName;
            dr["_Page"] = row.SourcePageIndex.HasValue ? (row.SourcePageIndex.Value + 1).ToString() : "";
            foreach (var h in headers)
                dr[h] = row.Cells.GetValueOrDefault(h)?.DisplayValue ?? "";
            table.Rows.Add(dr);
        }

        ResultsTable = table;
    }
}

public class BatchFileItem : ObservableObject
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public SupportedFileType FileType { get; set; }

    private string _status = "Pending";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}
