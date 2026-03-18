using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.Core.Services.Adapters;

public sealed class DocumentAdapterFactory
{
    private readonly IReadOnlyList<IDocumentAdapter> _adapters;
    private readonly ILogger<DocumentAdapterFactory> _logger;

    /// <summary>
    /// Creates a new factory that routes file types to the appropriate adapter.
    /// All <see cref="IDocumentAdapter"/> instances should be injected via DI.
    /// </summary>
    public DocumentAdapterFactory(
        IEnumerable<IDocumentAdapter> adapters,
        ILogger<DocumentAdapterFactory> logger)
    {
        _adapters = adapters?.ToList() ?? throw new ArgumentNullException(nameof(adapters));
        _logger = logger;

        _logger.LogInformation(
            "DocumentAdapterFactory initialised with {Count} adapter(s): {Types}",
            _adapters.Count,
            string.Join(", ", _adapters.Select(a => a.GetType().Name)));
    }

    /// <summary>
    /// Returns the appropriate adapter for the given file path, or null if no adapter can handle it.
    /// </summary>
    public IDocumentAdapter? GetAdapter(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("GetAdapter called with null or empty file path.");
            return null;
        }

        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(filePath));

        if (adapter != null)
        {
            _logger.LogDebug("Resolved adapter {Adapter} for file: {FilePath}",
                adapter.GetType().Name, filePath);
        }
        else
        {
            _logger.LogWarning("No adapter found for file: {FilePath}", filePath);
        }

        return adapter;
    }

    /// <summary>
    /// Returns the appropriate adapter for the given file type, or null if no adapter supports it.
    /// </summary>
    public IDocumentAdapter? GetAdapterForType(SupportedFileType fileType)
    {
        var adapter = _adapters.FirstOrDefault(a => a.SupportedTypes.Contains(fileType));

        if (adapter != null)
        {
            _logger.LogDebug("Resolved adapter {Adapter} for file type: {FileType}",
                adapter.GetType().Name, fileType);
        }
        else
        {
            _logger.LogWarning("No adapter found for file type: {FileType}", fileType);
        }

        return adapter;
    }

    /// <summary>
    /// Returns all file extensions supported across all registered adapters.
    /// </summary>
    public IReadOnlyList<SupportedFileType> GetAllSupportedTypes()
    {
        return _adapters
            .SelectMany(a => a.SupportedTypes)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Checks whether any registered adapter can handle the given file path.
    /// </summary>
    public bool CanHandle(string filePath)
    {
        return _adapters.Any(a => a.CanHandle(filePath));
    }
}
