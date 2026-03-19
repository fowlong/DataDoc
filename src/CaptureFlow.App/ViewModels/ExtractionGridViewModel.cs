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
    [ObservableProperty] private bool _autoClearOnExtract = true;
    [ObservableProperty] private string _selectedCsvGroup = "CSV 1";

    private List<ExtractionRow> _rows = [];
    private List<string> _headers = [];

    /// <summary>
    /// All CSV groups found in current results, for tab display.
    /// </summary>
    public ObservableCollection<string> CsvGroups { get; } = [];

    /// <summary>
    /// Per-group DataTables so we can display each CSV separately.
    /// </summary>
    private readonly Dictionary<string, DataTable> _groupTables = new();

    /// <summary>
    /// Per-group row lists for export.
    /// </summary>
    private readonly Dictionary<string, List<ExtractionRow>> _groupRows = new();

    public void LoadResults(List<ExtractionRow> rows, string sourceFileName, List<CaptureBox>? captureBoxes = null)
    {
        if (AutoClearOnExtract)
        {
            _rows = rows;
        }
        else
        {
            _rows.AddRange(rows);
        }

        SourceFileName = sourceFileName;

        // Determine CSV groups from capture boxes or default
        var groups = new HashSet<string> { "CSV 1" };
        if (captureBoxes != null)
        {
            foreach (var box in captureBoxes)
            {
                if (!string.IsNullOrWhiteSpace(box.CsvGroup))
                    groups.Add(box.CsvGroup);
            }
        }

        // Also detect groups from row headers if boxes had CsvGroup-prefixed headers
        // For now, we'll use CsvGroup from the boxes to partition columns

        // Build group membership: which headers belong to which group
        var headerToGroup = new Dictionary<string, string>();
        if (captureBoxes != null)
        {
            foreach (var box in captureBoxes)
            {
                var group = string.IsNullOrWhiteSpace(box.CsvGroup) ? "CSV 1" : box.CsvGroup;
                if (!string.IsNullOrWhiteSpace(box.OutputHeader))
                    headerToGroup[box.OutputHeader] = group;
            }
        }

        // Collect all unique headers
        _headers = _rows
            .SelectMany(r => r.Cells.Keys)
            .Distinct()
            .ToList();

        // Assign any unmapped headers to default group
        foreach (var h in _headers)
        {
            if (!headerToGroup.ContainsKey(h))
                headerToGroup[h] = "CSV 1";
        }

        // Rebuild group tables
        _groupTables.Clear();
        _groupRows.Clear();
        CsvGroups.Clear();

        foreach (var group in groups.OrderBy(g => g))
        {
            CsvGroups.Add(group);
            var groupHeaders = _headers.Where(h => headerToGroup.GetValueOrDefault(h, "CSV 1") == group).ToList();

            var table = new DataTable();
            var allColumns = new List<string>();
            if (_rows.Any(r => !string.IsNullOrEmpty(r.SourceFileName)))
                allColumns.Add("_SourceFile");
            if (_rows.Any(r => r.SourcePageIndex.HasValue))
                allColumns.Add("_Page");
            allColumns.AddRange(groupHeaders);

            foreach (var header in allColumns)
                table.Columns.Add(header, typeof(string));

            var groupRowList = new List<ExtractionRow>();
            foreach (var row in _rows)
            {
                // Only include rows that have at least one cell in this group
                if (!groupHeaders.Any(h => row.Cells.ContainsKey(h))) continue;

                var dataRow = table.NewRow();
                if (allColumns.Contains("_SourceFile"))
                    dataRow["_SourceFile"] = row.SourceFileName;
                if (allColumns.Contains("_Page"))
                    dataRow["_Page"] = row.SourcePageIndex?.ToString() ?? "";

                foreach (var header in groupHeaders)
                {
                    if (row.Cells.TryGetValue(header, out var cell))
                        dataRow[header] = cell.DisplayValue;
                    else
                        dataRow[header] = "";
                }

                table.Rows.Add(dataRow);
                groupRowList.Add(row);
            }

            _groupTables[group] = table;
            _groupRows[group] = groupRowList;
        }

        // Select first group and show its table
        if (CsvGroups.Count > 0 && !CsvGroups.Contains(SelectedCsvGroup))
            SelectedCsvGroup = CsvGroups[0];

        ShowGroupTable(SelectedCsvGroup);
    }

    partial void OnSelectedCsvGroupChanged(string value)
    {
        ShowGroupTable(value);
    }

    private void ShowGroupTable(string group)
    {
        if (_groupTables.TryGetValue(group, out var table))
        {
            ResultsTable = table;
            HasResults = table.Rows.Count > 0;
            RowCount = table.Rows.Count;
            ColumnCount = table.Columns.Count;
        }
        else
        {
            ResultsTable = null;
            HasResults = false;
            RowCount = 0;
            ColumnCount = 0;
        }
    }

    public List<ExtractionRow> GetRows() => _rows;

    public List<ExtractionRow> GetRowsForGroup(string group)
    {
        return _groupRows.GetValueOrDefault(group, []);
    }

    public DataTable? GetTableForGroup(string group)
    {
        return _groupTables.GetValueOrDefault(group);
    }

    [RelayCommand]
    private void AddRow()
    {
        if (ResultsTable == null) return;
        var row = ResultsTable.NewRow();
        ResultsTable.Rows.Add(row);
        RowCount = ResultsTable.Rows.Count;
    }

    [RelayCommand]
    private void DeleteSelectedRows(object? parameter)
    {
        if (ResultsTable == null || parameter is not System.Collections.IList selectedItems) return;

        var rowsToDelete = selectedItems
            .OfType<DataRowView>()
            .Select(drv => drv.Row)
            .ToList();

        foreach (var row in rowsToDelete)
            ResultsTable.Rows.Remove(row);

        RowCount = ResultsTable.Rows.Count;
        HasResults = RowCount > 0;
    }

    [RelayCommand]
    private void ClearResults()
    {
        ResultsTable?.Clear();
        _rows.Clear();
        _groupTables.Clear();
        _groupRows.Clear();
        CsvGroups.Clear();
        HasResults = false;
        RowCount = 0;
    }
}
