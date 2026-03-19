using System.Globalization;
using System.Text;
using CaptureFlow.Core.Models;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Core.Services.Extraction;

/// <summary>
/// Exports a list of <see cref="ExtractionRow"/> objects to a CSV file using CsvHelper.
/// Supports configurable separator, encoding, and optional source file/page columns.
/// </summary>
public class CsvExportService
{
    private readonly ILogger<CsvExportService> _logger;

    public CsvExportService(ILogger<CsvExportService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Exports extraction rows to a CSV file at the specified path.
    /// </summary>
    /// <param name="rows">The extraction rows to export.</param>
    /// <param name="outputPath">Destination file path.</param>
    /// <param name="options">Optional export configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ExportAsync(
        List<ExtractionRow> rows,
        string outputPath,
        CsvExportOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new CsvExportOptions();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = options.Separator,
            HasHeaderRecord = true,
            ShouldQuote = _ => true
        };

        var encoding = GetEncoding(options.EncodingName);

        // Collect all unique headers across all rows, preserving insertion order.
        var headers = new List<string>();
        var headerSet = new HashSet<string>(StringComparer.Ordinal);

        if (options.IncludeSourceFile)
        {
            headers.Add("SourceFile");
            headerSet.Add("SourceFile");
        }

        if (options.IncludeSourcePage)
        {
            headers.Add("SourcePage");
            headerSet.Add("SourcePage");
        }

        foreach (var row in rows)
        {
            foreach (var key in row.Cells.Keys)
            {
                if (headerSet.Add(key))
                    headers.Add(key);
            }
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var writer = new StreamWriter(outputPath, false, encoding);
        await using var csv = new CsvWriter(writer, config);

        // Write header row.
        foreach (var header in headers)
        {
            csv.WriteField(header);
        }

        await csv.NextRecordAsync();

        // Write data rows.
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var header in headers)
            {
                string value;

                if (header == "SourceFile" && options.IncludeSourceFile)
                {
                    value = row.SourceFileName;
                }
                else if (header == "SourcePage" && options.IncludeSourcePage)
                {
                    value = row.SourcePageIndex.HasValue ? (row.SourcePageIndex.Value + 1).ToString() : "";
                }
                else if (row.Cells.TryGetValue(header, out var cell))
                {
                    value = cell.DisplayValue;
                }
                else
                {
                    value = "";
                }

                csv.WriteField(value);
            }

            await csv.NextRecordAsync();
        }

        await csv.FlushAsync();

        _logger.LogInformation("Exported {RowCount} rows with {ColumnCount} columns to {Path}",
            rows.Count, headers.Count, outputPath);
    }

    /// <summary>
    /// Exports a DataTable directly to CSV — preserves user edits and row deletions.
    /// </summary>
    public async Task ExportTableAsync(
        System.Data.DataTable table,
        string outputPath,
        CsvExportOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new CsvExportOptions();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = options.Separator,
            HasHeaderRecord = true,
            ShouldQuote = _ => true
        };

        var encoding = GetEncoding(options.EncodingName);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var writer = new StreamWriter(outputPath, false, encoding);
        await using var csv = new CsvWriter(writer, config);

        // Write headers
        foreach (System.Data.DataColumn col in table.Columns)
            csv.WriteField(col.ColumnName);
        await csv.NextRecordAsync();

        // Write rows
        foreach (System.Data.DataRow row in table.Rows)
        {
            ct.ThrowIfCancellationRequested();
            foreach (System.Data.DataColumn col in table.Columns)
                csv.WriteField(row[col]?.ToString() ?? "");
            await csv.NextRecordAsync();
        }

        await csv.FlushAsync();

        _logger.LogInformation("Exported {RowCount} rows with {ColumnCount} columns to {Path}",
            table.Rows.Count, table.Columns.Count, outputPath);
    }

    private static Encoding GetEncoding(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        return name.ToUpperInvariant() switch
        {
            "UTF-8" or "UTF8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "UTF-8-NO-BOM" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "ASCII" => Encoding.ASCII,
            "UTF-16" or "UNICODE" => Encoding.Unicode,
            _ => Encoding.GetEncoding(name)
        };
    }
}

/// <summary>
/// Configuration options for CSV export.
/// </summary>
public class CsvExportOptions
{
    /// <summary>Column separator. Defaults to comma.</summary>
    public string Separator { get; set; } = ",";

    /// <summary>Output encoding name. Defaults to UTF-8 with BOM.</summary>
    public string? EncodingName { get; set; }

    /// <summary>Whether to include a SourceFile column.</summary>
    public bool IncludeSourceFile { get; set; } = true;

    /// <summary>Whether to include a SourcePage column.</summary>
    public bool IncludeSourcePage { get; set; } = true;
}
