using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using CaptureFlow.Core.Services.Transforms;
using CaptureFlow.Core.Services.Validation;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Core.Services.Extraction;

/// <summary>
/// Core extraction service. Given a <see cref="SourceDocument"/>, capture boxes, and repeat groups,
/// it extracts text from each box region, applies transforms and validation, and builds
/// <see cref="ExtractionRow"/> results respecting each box's <see cref="RowTargetMode"/>.
/// </summary>
public class ExtractionService : IExtractionService
{
    private readonly TransformService _transformService;
    private readonly ValidationService _validationService;
    private readonly ILogger<ExtractionService> _logger;

    public ExtractionService(
        TransformService transformService,
        ValidationService validationService,
        ILogger<ExtractionService> logger)
    {
        _transformService = transformService;
        _validationService = validationService;
        _logger = logger;
    }

    public async Task<List<ExtractionRow>> ExtractAsync(
        SourceDocument document,
        IReadOnlyList<CaptureBox> captureBoxes,
        IReadOnlyList<RepeatGroup> repeatGroups,
        CancellationToken ct = default)
    {
        return await Task.Run(() => ExtractCore(document, captureBoxes, repeatGroups, ct), ct);
    }

    private List<ExtractionRow> ExtractCore(
        SourceDocument document,
        IReadOnlyList<CaptureBox> captureBoxes,
        IReadOnlyList<RepeatGroup> repeatGroups,
        CancellationToken ct)
    {
        var enabledBoxes = captureBoxes.Where(b => b.Enabled).OrderBy(b => b.SortOrder).ToList();
        var enabledGroups = repeatGroups.Where(g => g.Enabled).ToDictionary(g => g.Id);

        // Phase 1: Extract raw values for every capture box.
        var rawResults = new List<(CaptureBox Box, List<ExtractionResult> Results)>();

        foreach (var box in enabledBoxes)
        {
            ct.ThrowIfCancellationRequested();

            if (box.PageIndex < 0 || box.PageIndex >= document.Pages.Count)
            {
                _logger.LogWarning("CaptureBox '{Name}' references page {Page} which does not exist",
                    box.Name, box.PageIndex);
                continue;
            }

            var results = ExtractCaptureBox(document, box, enabledGroups);
            rawResults.Add((box, results));
        }

        // Phase 2: Build rows based on RowTargetMode.
        var rows = BuildRows(document, rawResults, enabledGroups);

        _logger.LogInformation("Extracted {RowCount} rows with {BoxCount} capture boxes from {FileName}",
            rows.Count, enabledBoxes.Count, document.FileName);

        return rows;
    }

    private List<ExtractionResult> ExtractCaptureBox(
        SourceDocument document,
        CaptureBox box,
        Dictionary<string, RepeatGroup> groups)
    {
        var page = document.Pages[box.PageIndex];

        // If the box belongs to a repeat group, extract one result per detected row.
        if (!string.IsNullOrEmpty(box.RepeatGroupId) &&
            groups.TryGetValue(box.RepeatGroupId, out var group))
        {
            return ExtractRepeatGroupRows(document, box, page, group);
        }

        // Single-value extraction.
        var rawValue = TextExtractionHelper.ExtractText(page, box.Rect, box.ExtractionMode);
        var confidence = TextExtractionHelper.ComputeConfidence(page, box.Rect, box.ExtractionMode);

        if (string.IsNullOrEmpty(rawValue))
            rawValue = box.DefaultValue ?? box.FallbackValue;

        var transformed = _transformService.Apply(rawValue, box.TransformRules);
        var validation = _validationService.Validate(transformed, box.ValidationRules);

        var result = new ExtractionResult
        {
            SourceDocumentId = document.Id,
            SourceFileName = document.FileName,
            PageIndex = box.PageIndex,
            Header = box.OutputHeader,
            CaptureBoxId = box.Id,
            RawValue = rawValue,
            TransformedValue = transformed,
            Confidence = confidence,
            ValidationState = validation,
            RowIndex = 0,
            RepeatGroupId = box.RepeatGroupId
        };

        return [result];
    }

    private List<ExtractionResult> ExtractRepeatGroupRows(
        SourceDocument document,
        CaptureBox box,
        DocumentPage page,
        RepeatGroup group)
    {
        var rowRects = ComputeRepeatGroupRowRects(group);
        var results = new List<ExtractionResult>();

        for (int rowIdx = 0; rowIdx < rowRects.Count; rowIdx++)
        {
            var rowRegion = rowRects[rowIdx];

            // Intersect the capture box rect with the row region to get the per-row rect.
            var cellRect = IntersectRects(box.Rect, rowRegion);
            if (cellRect == null)
                continue;

            var rawValue = TextExtractionHelper.ExtractText(page, cellRect, box.ExtractionMode);
            var confidence = TextExtractionHelper.ComputeConfidence(page, cellRect, box.ExtractionMode);

            if (string.IsNullOrEmpty(rawValue))
                rawValue = box.DefaultValue ?? box.FallbackValue;

            var transformed = _transformService.Apply(rawValue, box.TransformRules);
            var validation = _validationService.Validate(transformed, box.ValidationRules);

            results.Add(new ExtractionResult
            {
                SourceDocumentId = document.Id,
                SourceFileName = document.FileName,
                PageIndex = box.PageIndex,
                Header = box.OutputHeader,
                CaptureBoxId = box.Id,
                RawValue = rawValue,
                TransformedValue = transformed,
                Confidence = confidence,
                ValidationState = validation,
                RowIndex = rowIdx,
                RepeatGroupId = group.Id
            });
        }

        return results;
    }

    private static List<NormalisedRect> ComputeRepeatGroupRowRects(RepeatGroup group)
    {
        var region = group.RegionRect;
        var rows = new List<NormalisedRect>();

        switch (group.RowDetectionMode)
        {
            case RowDetectionMode.FixedHeight:
            {
                var rowHeight = group.FixedRowHeight ?? 0.03;
                double y = region.Y;
                while (y + rowHeight <= region.Bottom + 0.0001)
                {
                    rows.Add(new NormalisedRect(region.X, y, region.Width, rowHeight));
                    y += rowHeight;
                }

                if (rows.Count == 0)
                    rows.Add(region);

                break;
            }

            case RowDetectionMode.Manual:
            {
                var count = group.ExpectedRowCount ?? 1;
                var rowHeight = region.Height / count;
                for (int i = 0; i < count; i++)
                {
                    rows.Add(new NormalisedRect(region.X, region.Y + i * rowHeight, region.Width, rowHeight));
                }

                break;
            }

            case RowDetectionMode.DynamicByContent:
            default:
            {
                // Fallback: treat the entire region as one row.
                rows.Add(region);
                break;
            }
        }

        return rows;
    }

    private static NormalisedRect? IntersectRects(NormalisedRect a, NormalisedRect b)
    {
        double x = Math.Max(a.X, b.X);
        double y = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);

        if (right <= x || bottom <= y)
            return null;

        return new NormalisedRect(x, y, right - x, bottom - y);
    }

    private List<ExtractionRow> BuildRows(
        SourceDocument document,
        List<(CaptureBox Box, List<ExtractionResult> Results)> rawResults,
        Dictionary<string, RepeatGroup> groups)
    {
        var rows = new List<ExtractionRow>();

        // Ensure at least one document-level row exists.
        var docRow = CreateRow(0, document);
        rows.Add(docRow);

        // Track repeat group rows separately, keyed by group id.
        var repeatGroupRows = new Dictionary<string, List<ExtractionRow>>();

        int nextNewRowIndex = 1;

        foreach (var (box, results) in rawResults)
        {
            switch (box.RowTargetMode)
            {
                case RowTargetMode.DocumentRow:
                {
                    // All results go into the single document row (row 0).
                    foreach (var result in results)
                    {
                        result.RowIndex = 0;
                        docRow.Cells[result.Header] = result;
                    }
                    break;
                }

                case RowTargetMode.RepeatGroupRow:
                {
                    var groupId = box.RepeatGroupId ?? "";
                    if (!repeatGroupRows.TryGetValue(groupId, out var groupRows))
                    {
                        groupRows = [];
                        repeatGroupRows[groupId] = groupRows;
                    }

                    foreach (var result in results)
                    {
                        // Ensure enough rows exist for this repeat group.
                        while (groupRows.Count <= result.RowIndex)
                        {
                            groupRows.Add(CreateRow(groupRows.Count, document, box.PageIndex));
                        }

                        groupRows[result.RowIndex].Cells[result.Header] = result;
                    }
                    break;
                }

                case RowTargetMode.StartNewRow:
                {
                    foreach (var result in results)
                    {
                        var newRow = CreateRow(nextNewRowIndex++, document, box.PageIndex);
                        result.RowIndex = newRow.RowIndex;
                        newRow.Cells[result.Header] = result;
                        rows.Add(newRow);
                    }
                    break;
                }

                case RowTargetMode.AppendToPrevious:
                {
                    foreach (var result in results)
                    {
                        var lastRow = rows[^1];
                        result.RowIndex = lastRow.RowIndex;
                        lastRow.Cells[result.Header] = result;
                    }
                    break;
                }

                case RowTargetMode.CopyToAllRows:
                {
                    // Value will be copied to all rows at the end.
                    // For now place in doc row; we copy after building all rows.
                    foreach (var result in results.Take(1))
                    {
                        docRow.Cells[result.Header] = result;
                    }
                    break;
                }
            }
        }

        // Merge repeat group rows into the main row list.
        foreach (var groupRows in repeatGroupRows.Values)
        {
            rows.AddRange(groupRows);
        }

        // Handle CopyToAllRows: copy cells from the doc row to all other rows.
        var copyHeaders = rawResults
            .Where(r => r.Box.RowTargetMode == RowTargetMode.CopyToAllRows)
            .SelectMany(r => r.Results.Select(res => res.Header))
            .Distinct()
            .ToList();

        if (copyHeaders.Count > 0 && rows.Count > 1)
        {
            foreach (var row in rows)
            {
                foreach (var header in copyHeaders)
                {
                    if (docRow.Cells.TryGetValue(header, out var sourceResult) &&
                        !row.Cells.ContainsKey(header))
                    {
                        row.Cells[header] = new ExtractionResult
                        {
                            SourceDocumentId = sourceResult.SourceDocumentId,
                            SourceFileName = sourceResult.SourceFileName,
                            PageIndex = sourceResult.PageIndex,
                            Header = sourceResult.Header,
                            CaptureBoxId = sourceResult.CaptureBoxId,
                            RawValue = sourceResult.RawValue,
                            TransformedValue = sourceResult.TransformedValue,
                            Confidence = sourceResult.Confidence,
                            ValidationState = sourceResult.ValidationState,
                            RowIndex = row.RowIndex,
                            RepeatGroupId = sourceResult.RepeatGroupId
                        };
                    }
                }
            }
        }

        // Re-index rows sequentially.
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].RowIndex = i;
        }

        return rows;
    }

    private static ExtractionRow CreateRow(int rowIndex, SourceDocument document, int? pageIndex = null)
    {
        return new ExtractionRow
        {
            RowIndex = rowIndex,
            SourceDocumentId = document.Id,
            SourceFileName = document.FileName,
            SourcePageIndex = pageIndex
        };
    }
}
