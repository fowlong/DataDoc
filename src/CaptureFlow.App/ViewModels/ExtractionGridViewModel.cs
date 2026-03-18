using System.Collections.ObjectModel;
using System.Data;
using CaptureFlow.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CaptureFlow.App.ViewModels;

public partial class ExtractionGridViewModel : ObservableObject
{
    [ObservableProperty] private DataTable? _resultsTable;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private int _rowCount;
    [ObservableProperty] private int _columnCount;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _sourceFileName = "";

    private List<ExtractionRow> _rows = [];
    private List<string> _headers = [];

    public void LoadResults(List<ExtractionRow> rows, string sourceFileName)
    {
        _rows = rows;
        SourceFileName = sourceFileName;

        var table = new DataTable();

        // Collect all unique headers
        _headers = rows
            .SelectMany(r => r.Cells.Keys)
            .Distinct()
            .ToList();

        // Add source columns if present
        var allColumns = new List<string>();
        if (rows.Any(r => !string.IsNullOrEmpty(r.SourceFileName)))
            allColumns.Add("_SourceFile");
        if (rows.Any(r => r.SourcePageIndex.HasValue))
            allColumns.Add("_Page");
        allColumns.AddRange(_headers);

        foreach (var header in allColumns)
            table.Columns.Add(header, typeof(string));

        foreach (var row in rows)
        {
            var dataRow = table.NewRow();
            if (allColumns.Contains("_SourceFile"))
                dataRow["_SourceFile"] = row.SourceFileName;
            if (allColumns.Contains("_Page"))
                dataRow["_Page"] = row.SourcePageIndex?.ToString() ?? "";

            foreach (var header in _headers)
            {
                if (row.Cells.TryGetValue(header, out var cell))
                    dataRow[header] = cell.DisplayValue;
                else
                    dataRow[header] = "";
            }

            table.Rows.Add(dataRow);
        }

        ResultsTable = table;
        HasResults = table.Rows.Count > 0;
        RowCount = table.Rows.Count;
        ColumnCount = table.Columns.Count;
    }

    public List<ExtractionRow> GetRows() => _rows;

    [RelayCommand]
    private void AddRow()
    {
        if (ResultsTable == null) return;
        var row = ResultsTable.NewRow();
        ResultsTable.Rows.Add(row);
        RowCount = ResultsTable.Rows.Count;
    }

    [RelayCommand]
    private void DeleteRow()
    {
        // Handled via DataGrid selection in view
    }

    [RelayCommand]
    private void DuplicateRow()
    {
        // Handled via DataGrid selection in view
    }

    [RelayCommand]
    private void ClearResults()
    {
        ResultsTable?.Clear();
        _rows.Clear();
        HasResults = false;
        RowCount = 0;
    }
}
