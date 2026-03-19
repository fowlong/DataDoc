using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CaptureFlow.App.ViewModels;

public partial class MergeViewModel : ObservableObject
{
    private readonly IMergeService _mergeService;
    private readonly ILogger<MergeViewModel> _logger;

    [ObservableProperty] private string _csvFilePath = "";
    [ObservableProperty] private string _templateFilePath = "";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private string _fileNamePattern = "output_{{RowNumber}}";
    [ObservableProperty] private MergeOutputFormat _outputFormat = MergeOutputFormat.Docx;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _totalRows;
    [ObservableProperty] private int _processedRows;
    [ObservableProperty] private byte[]? _previewImage;
    [ObservableProperty] private DataTable? _csvPreviewTable;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private int _previewRowIndex = 1;

    public ObservableCollection<string> CsvHeaders { get; } = [];
    public ObservableCollection<string> TemplatePlaceholders { get; } = [];
    public ObservableCollection<MergeFieldMapping> FieldMappings { get; } = [];

    private List<Dictionary<string, string>> _csvRows = [];

    public MergeViewModel(IMergeService mergeService, ILogger<MergeViewModel> logger)
    {
        _mergeService = mergeService;
        _logger = logger;
    }

    [RelayCommand]
    private async Task LoadCsv()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            Title = "Select CSV Data File"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            CsvFilePath = dialog.FileName;
            await LoadCsvDataAsync(dialog.FileName);
            StatusText = $"Loaded {_csvRows.Count} rows from CSV";
            AutoMapFields();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load CSV");
            StatusText = $"Failed to load CSV: {ex.Message}";
        }
    }

    private async Task LoadCsvDataAsync(string filePath)
    {
        _csvRows.Clear();
        CsvHeaders.Clear();

        await Task.Run(() =>
        {
            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null
            });

            csv.Read();
            csv.ReadHeader();

            if (csv.HeaderRecord != null)
            {
                foreach (var h in csv.HeaderRecord)
                    CsvHeaders.Add(h);
            }

            while (csv.Read())
            {
                var row = new Dictionary<string, string>();
                foreach (var header in CsvHeaders)
                {
                    row[header] = csv.GetField(header) ?? "";
                }
                _csvRows.Add(row);
            }
        });

        TotalRows = _csvRows.Count;

        // Build preview table
        var table = new DataTable();
        foreach (var h in CsvHeaders)
            table.Columns.Add(h, typeof(string));

        foreach (var row in _csvRows.Take(50))
        {
            var dr = table.NewRow();
            foreach (var h in CsvHeaders)
                dr[h] = row.GetValueOrDefault(h, "");
            table.Rows.Add(dr);
        }

        CsvPreviewTable = table;
    }

    [RelayCommand]
    private async Task LoadTemplate()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Document Templates (*.docx;*.pdf)|*.docx;*.pdf|All Files (*.*)|*.*",
            Title = "Select Document Template"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            TemplateFilePath = dialog.FileName;

            var placeholders = await _mergeService.GetTemplatePlaceholdersAsync(dialog.FileName);
            TemplatePlaceholders.Clear();
            foreach (var p in placeholders) TemplatePlaceholders.Add(p);

            StatusText = $"Template loaded: {Path.GetFileName(dialog.FileName)} ({placeholders.Count} placeholders)";
            AutoMapFields();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load template");
            StatusText = $"Failed to load template: {ex.Message}";
        }
    }

    private void AutoMapFields()
    {
        FieldMappings.Clear();

        foreach (var placeholder in TemplatePlaceholders)
        {
            var mapping = new MergeFieldMapping
            {
                TemplatePlaceholder = placeholder,
                CsvHeader = CsvHeaders.FirstOrDefault(h =>
                    h.Equals(placeholder, StringComparison.OrdinalIgnoreCase) ||
                    h.Replace(" ", "").Equals(placeholder.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    ?? ""
            };
            FieldMappings.Add(mapping);
        }
    }

    [RelayCommand]
    private async Task Preview()
    {
        if (string.IsNullOrEmpty(TemplateFilePath) || _csvRows.Count == 0) return;

        var rowIndex = Math.Clamp(PreviewRowIndex - 1, 0, _csvRows.Count - 1);

        try
        {
            var fieldValues = BuildFieldValues(_csvRows[rowIndex]);
            var previewBytes = await _mergeService.GeneratePreviewAsync(
                TemplateFilePath, fieldValues, OutputFormat);
            PreviewImage = previewBytes;
            HasPreview = previewBytes is { Length: > 0 };
            StatusText = $"Preview generated for row {rowIndex + 1}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preview failed");
            StatusText = $"Preview failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PreviewRow()
    {
        await Preview();
    }

    [RelayCommand]
    private async Task GenerateAll()
    {
        if (string.IsNullOrEmpty(TemplateFilePath) || _csvRows.Count == 0) return;

        if (string.IsNullOrEmpty(OutputDirectory))
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select output directory"
            };
            if (dialog.ShowDialog() != true) return;
            OutputDirectory = dialog.FolderName;
        }

        IsProcessing = true;
        ProcessedRows = 0;

        var progress = new Progress<int>(count =>
        {
            ProcessedRows = count;
            Progress = TotalRows > 0 ? (double)count / TotalRows * 100 : 0;
            StatusText = $"Generated {count} of {TotalRows} documents...";
        });

        try
        {
            var allFieldValues = _csvRows.Select(BuildFieldValues).ToList();
            var outputFiles = await _mergeService.GenerateBulkAsync(
                TemplateFilePath, allFieldValues, OutputFormat,
                OutputDirectory, FileNamePattern, progress);

            StatusText = $"Generated {outputFiles.Count} documents in {OutputDirectory}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk generation failed");
            StatusText = $"Generation failed: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void SelectOutputDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select output directory for generated documents"
        };
        if (dialog.ShowDialog() == true)
            OutputDirectory = dialog.FolderName;
    }

    private Dictionary<string, string> BuildFieldValues(Dictionary<string, string> csvRow)
    {
        var result = new Dictionary<string, string>();
        foreach (var mapping in FieldMappings)
        {
            if (!string.IsNullOrEmpty(mapping.CsvHeader) && csvRow.TryGetValue(mapping.CsvHeader, out var value))
                result[mapping.TemplatePlaceholder] = value;
            else
                result[mapping.TemplatePlaceholder] = "";
        }
        return result;
    }

    /// <summary>
    /// Load extraction results directly without requiring CSV export/import.
    /// Converts ExtractionRow data into the same internal format as CSV load.
    /// </summary>
    public void LoadFromExtractionResults(List<ExtractionRow> rows)
    {
        _csvRows.Clear();
        CsvHeaders.Clear();

        if (rows.Count == 0)
        {
            StatusText = "No extraction results to load";
            return;
        }

        // Collect all unique headers from extraction rows
        var headers = rows
            .SelectMany(r => r.Cells.Keys)
            .Distinct()
            .OrderBy(h => h)
            .ToList();

        foreach (var h in headers)
            CsvHeaders.Add(h);

        // Convert ExtractionRows to Dictionary<string, string> rows
        foreach (var row in rows)
        {
            var csvRow = new Dictionary<string, string>();
            foreach (var header in headers)
            {
                csvRow[header] = row.Cells.TryGetValue(header, out var cell)
                    ? cell.DisplayValue
                    : "";
            }
            _csvRows.Add(csvRow);
        }

        TotalRows = _csvRows.Count;
        CsvFilePath = "(extraction results)";

        // Build preview table
        var table = new DataTable();
        foreach (var h in headers)
            table.Columns.Add(h, typeof(string));

        foreach (var row in _csvRows.Take(50))
        {
            var dr = table.NewRow();
            foreach (var h in headers)
                dr[h] = row.GetValueOrDefault(h, "");
            table.Rows.Add(dr);
        }

        CsvPreviewTable = table;
        StatusText = $"Loaded {_csvRows.Count} rows from extraction results";
        AutoMapFields();
    }
}
