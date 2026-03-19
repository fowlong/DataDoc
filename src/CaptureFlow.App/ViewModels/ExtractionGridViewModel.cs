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
    /// Header-to-group mapping built from capture boxes.
    /// </summary>
    private Dictionary<string, string> _headerToGroup = new();

    public void LoadResults(List<ExtractionRow> rows, string sourceFileName, List<CaptureBox>? captureBoxes = null)
    {
        if (AutoClearOnExtract)
        {
            _rows = new List<ExtractionRow>(rows);
        }
        else
        {
            _rows.AddRange(rows);
        }

        SourceFileName = sourceFileName;

        // Build header-to-group mapping from capture boxes
        _headerToGroup = new Dictionary<string, string>();
        if (captureBoxes != null)
        {
            foreach (var box in captureBoxes)
            {
                var group = string.IsNullOrWhiteSpace(box.CsvGroup) ? "CSV 1" : box.CsvGroup;
                if (!string.IsNullOrWhiteSpace(box.OutputHeader))
                    _headerToGroup[box.OutputHeader] = group;
            }
        }

        // Collect all unique headers from actual data
        _headers = _rows
            .SelectMany(r => r.Cells.Keys)
            .Distinct()
            .ToList();

        // Assign any unmapped headers to default group
        foreach (var h in _headers)
        {
            if (!_headerToGroup.ContainsKey(h))
                _headerToGroup[h] = "CSV 1";
        }

        // Determine all groups
        var groups = _headerToGroup.Values.Distinct().OrderBy(g => g).ToList();
        if (groups.Count == 0) groups.Add("CSV 1");

        // Check if we need source columns
        bool hasSourceFile = _rows.Any(r => !string.IsNullOrEmpty(r.SourceFileName));
        bool hasSourcePage = _rows.Any(r => r.SourcePageIndex.HasValue);

        // Build per-group tables — ALL rows appear in each group, just different columns
        _groupTables.Clear();
        CsvGroups.Clear();

        foreach (var group in groups)
        {
            CsvGroups.Add(group);
            var groupHeaders = _headers
                .Where(h => _headerToGroup.GetValueOrDefault(h, "CSV 1") == group)
                .ToList();

            var table = new DataTable();

            // Add source tracking columns
            if (hasSourceFile) table.Columns.Add("_SourceFile", typeof(string));
            if (hasSourcePage) table.Columns.Add("_Page", typeof(string));

            // Add data columns for this group
            foreach (var header in groupHeaders)
                table.Columns.Add(header, typeof(string));

            // Add ALL rows — each group is a different column view of the same data
            foreach (var row in _rows)
            {
                var dataRow = table.NewRow();
                if (hasSourceFile) dataRow["_SourceFile"] = row.SourceFileName;
                if (hasSourcePage) dataRow["_Page"] = row.SourcePageIndex.HasValue ? (row.SourcePageIndex.Value + 1).ToString() : "";

                foreach (var header in groupHeaders)
                {
                    dataRow[header] = row.Cells.TryGetValue(header, out var cell)
                        ? cell.DisplayValue
                        : "";
                }

                table.Rows.Add(dataRow);
            }

            _groupTables[group] = table;
        }

        // Select current group or first available
        if (!CsvGroups.Contains(SelectedCsvGroup))
            SelectedCsvGroup = CsvGroups.FirstOrDefault() ?? "CSV 1";
        else
            ShowGroupTable(SelectedCsvGroup); // force refresh even if group name unchanged
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
        ResultsTable = null;
        _rows.Clear();
        _groupTables.Clear();
        _headerToGroup.Clear();
        CsvGroups.Clear();
        HasResults = false;
        RowCount = 0;
        ColumnCount = 0;
    }
}
