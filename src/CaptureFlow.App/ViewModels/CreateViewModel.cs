using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text.Json;
using CaptureFlow.Core.Models;
using CaptureFlow.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CaptureFlow.App.ViewModels;

/// <summary>
/// Bridge interface for WebView2 JS interop. Implemented by CreatePanel code-behind.
/// </summary>
public interface IDesignerBridge
{
    Task LoadBasePdfAsync(string base64Pdf);
    Task<string> GetTemplateJsonAsync();
    Task SetTemplateJsonAsync(string json);
    Task<List<string>> GetFieldNamesAsync();
    Task<byte[]> GenerateSinglePdfAsync(string inputsJson);
    Task InsertMergeFieldAsync(string headerName);
    bool IsReady { get; }
    event Action? OnReady;
    event Action<int, int>? OnGenerationProgress;
}

public partial class CreateViewModel : ObservableObject
{
    private readonly DocxToPdfConverter _docxToPdfConverter;
    private readonly ILogger<CreateViewModel> _logger;

    [ObservableProperty] private string _baseDocumentPath = "";
    [ObservableProperty] private string _baseDocumentType = "";
    [ObservableProperty] private string _csvFilePath = "";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private string _fileNamePattern = "output_{{RowNumber}}";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private int _totalRows;
    [ObservableProperty] private int _processedRows;
    [ObservableProperty] private DataTable? _csvPreviewTable;
    [ObservableProperty] private bool _isDesignerReady;
    [ObservableProperty] private int _fieldCount;
    [ObservableProperty] private string _templateFilePath = "";
    [ObservableProperty] private string? _selectedMergeHeader;
    [ObservableProperty] private bool _useAllRows = true;
    [ObservableProperty] private bool _useSpecificRows;
    [ObservableProperty] private string _rowSelectionPattern = "";

    public ObservableCollection<string> CsvHeaders { get; } = [];

    private List<Dictionary<string, string>> _csvRows = [];
    private IDesignerBridge? _designerBridge;

    public IDesignerBridge? DesignerBridge
    {
        get => _designerBridge;
        set
        {
            _designerBridge = value;
            if (value != null)
            {
                value.OnReady += () =>
                {
                    IsDesignerReady = true;
                    StatusText = "Designer ready";
                };
                value.OnGenerationProgress += (current, total) =>
                {
                    ProcessedRows = current;
                    Progress = total > 0 ? (double)current / total * 100 : 0;
                    StatusText = $"Generated {current} of {total} documents...";
                };
            }
        }
    }

    public CreateViewModel(DocxToPdfConverter docxToPdfConverter, ILogger<CreateViewModel> logger)
    {
        _docxToPdfConverter = docxToPdfConverter;
        _logger = logger;
    }

    [RelayCommand]
    private async Task LoadBaseDocument()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Documents (*.pdf;*.docx)|*.pdf;*.docx|PDF Files (*.pdf)|*.pdf|DOCX Files (*.docx)|*.docx|All Files (*.*)|*.*",
            Title = "Select Base Document"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            IsProcessing = true;
            StatusText = $"Loading base document: {Path.GetFileName(dialog.FileName)}...";
            BaseDocumentPath = dialog.FileName;

            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            byte[] pdfBytes;

            if (ext == ".docx")
            {
                BaseDocumentType = "DOCX (converted to PDF)";
                StatusText = "Converting DOCX to PDF...";
                pdfBytes = await _docxToPdfConverter.ConvertAsync(dialog.FileName);
            }
            else if (ext == ".pdf")
            {
                BaseDocumentType = "PDF";
                pdfBytes = await File.ReadAllBytesAsync(dialog.FileName);
            }
            else
            {
                StatusText = $"Unsupported file type: {ext}";
                return;
            }

            var base64 = Convert.ToBase64String(pdfBytes);

            if (_designerBridge != null)
            {
                if (!_designerBridge.IsReady)
                    StatusText = "Waiting for designer to initialize...";
                await _designerBridge.LoadBasePdfAsync(base64);
                StatusText = $"Loaded base document: {Path.GetFileName(dialog.FileName)}";
            }
            else
            {
                StatusText = "Designer not ready yet. Please wait and try again.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load base document");
            StatusText = $"Failed to load document: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
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
                    row[header] = csv.GetField(header) ?? "";
                _csvRows.Add(row);
            }
        });

        TotalRows = _csvRows.Count;

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
    private async Task SaveTemplate()
    {
        if (_designerBridge == null || !_designerBridge.IsReady)
        {
            StatusText = "Designer not ready";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Template Files (*.json)|*.json",
            Title = "Save Create Template",
            FileName = "create_template.json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var templateJson = await _designerBridge.GetTemplateJsonAsync();
            await File.WriteAllTextAsync(dialog.FileName, templateJson);
            TemplateFilePath = dialog.FileName;
            StatusText = $"Template saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save template");
            StatusText = $"Save error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadTemplate()
    {
        if (_designerBridge == null || !_designerBridge.IsReady)
        {
            StatusText = "Designer not ready";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Template Files (*.json)|*.json",
            Title = "Load Create Template"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var templateJson = await File.ReadAllTextAsync(dialog.FileName);
            await _designerBridge.SetTemplateJsonAsync(templateJson);
            TemplateFilePath = dialog.FileName;
            StatusText = $"Template loaded: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load template");
            StatusText = $"Load error: {ex.Message}";
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

    [RelayCommand]
    private async Task GenerateAll()
    {
        if (_designerBridge == null || !_designerBridge.IsReady)
        {
            StatusText = "Designer not ready";
            return;
        }

        if (_csvRows.Count == 0)
        {
            StatusText = "No CSV data loaded";
            return;
        }

        if (string.IsNullOrEmpty(OutputDirectory))
        {
            var dialog = new OpenFolderDialog { Title = "Select output directory" };
            if (dialog.ShowDialog() != true) return;
            OutputDirectory = dialog.FolderName;
        }

        if (!Directory.Exists(OutputDirectory))
            Directory.CreateDirectory(OutputDirectory);

        IsProcessing = true;
        ProcessedRows = 0;
        Progress = 0;

        try
        {
            var fieldNames = await _designerBridge.GetFieldNamesAsync();

            // Determine which rows to process
            List<int> rowIndices;
            if (UseSpecificRows && !string.IsNullOrWhiteSpace(RowSelectionPattern))
            {
                var selected = ParseRowSelection(RowSelectionPattern, _csvRows.Count);
                rowIndices = selected.OrderBy(x => x).ToList();
                if (rowIndices.Count == 0)
                {
                    StatusText = "No valid rows matched the selection pattern";
                    return;
                }
            }
            else
            {
                rowIndices = Enumerable.Range(0, _csvRows.Count).ToList();
            }

            _logger.LogInformation("Generating {Count} PDFs with {FieldCount} fields",
                rowIndices.Count, fieldNames.Count);

            int generated = 0;
            foreach (var i in rowIndices)
            {
                var row = _csvRows[i];

                // Build inputs object matching field names to CSV values
                var inputs = new Dictionary<string, string>();
                foreach (var fieldName in fieldNames)
                {
                    inputs[fieldName] = row.GetValueOrDefault(fieldName, "");
                }

                var inputsJson = JsonSerializer.Serialize(inputs);
                var pdfBytes = await _designerBridge.GenerateSinglePdfAsync(inputsJson);

                var fileName = ResolveFileNamePattern(FileNamePattern, row, i + 1);
                if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    fileName += ".pdf";

                var outputPath = Path.Combine(OutputDirectory, fileName);
                outputPath = GetUniqueFilePath(outputPath);

                await File.WriteAllBytesAsync(outputPath, pdfBytes);

                generated++;
                ProcessedRows = generated;
                Progress = (double)generated / rowIndices.Count * 100;
                StatusText = $"Generated {generated} of {rowIndices.Count} documents...";
            }

            StatusText = $"Generated {rowIndices.Count} documents in {OutputDirectory}";
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

    /// <summary>
    /// Load extraction results directly from the extraction tab.
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

        var headers = rows
            .SelectMany(r => r.Cells.Keys)
            .Distinct()
            .OrderBy(h => h)
            .ToList();

        foreach (var h in headers)
            CsvHeaders.Add(h);

        foreach (var row in rows)
        {
            var csvRow = new Dictionary<string, string>();
            foreach (var header in headers)
                csvRow[header] = row.Cells.TryGetValue(header, out var cell) ? cell.DisplayValue : "";
            _csvRows.Add(csvRow);
        }

        TotalRows = _csvRows.Count;
        CsvFilePath = "(extraction results)";

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
    }

    [RelayCommand]
    private async Task InsertMergeField()
    {
        if (_designerBridge == null || !_designerBridge.IsReady)
        {
            StatusText = "Designer not ready";
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedMergeHeader))
        {
            StatusText = "Select a CSV header first";
            return;
        }

        await _designerBridge.InsertMergeFieldAsync(SelectedMergeHeader);
        StatusText = $"Inserted merge field: {SelectedMergeHeader}";
    }

    public void UpdateFieldCount(int count)
    {
        FieldCount = count;
    }

    /// <summary>
    /// Parse row selection pattern like "1,3-5,8" into a set of 0-based row indices.
    /// Input uses 1-based numbering (user-facing).
    /// </summary>
    internal HashSet<int> ParseRowSelection(string pattern, int totalRows)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(pattern)) return result;

        foreach (var part in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var range = part.Split('-', 2);
                if (int.TryParse(range[0].Trim(), out var start) && int.TryParse(range[1].Trim(), out var end))
                {
                    for (int i = Math.Max(1, start); i <= Math.Min(totalRows, end); i++)
                        result.Add(i - 1); // Convert to 0-based
                }
            }
            else if (int.TryParse(part, out var num) && num >= 1 && num <= totalRows)
            {
                result.Add(num - 1); // Convert to 0-based
            }
        }

        return result;
    }

    private static string ResolveFileNamePattern(string pattern, Dictionary<string, string> fieldValues, int rowNumber)
    {
        var result = pattern.Replace("{{RowNumber}}", rowNumber.ToString(), StringComparison.OrdinalIgnoreCase);

        foreach (var kvp in fieldValues)
        {
            result = result.Replace($"{{{{{kvp.Key}}}}}", SanitizeFileName(kvp.Value));
        }

        return result;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private static string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;

        var dir = Path.GetDirectoryName(filePath)!;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        int counter = 1;

        string newPath;
        do { newPath = Path.Combine(dir, $"{name}_{counter++}{ext}"); }
        while (File.Exists(newPath));

        return newPath;
    }
}
