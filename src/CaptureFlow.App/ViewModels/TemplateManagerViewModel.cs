using System.Collections.ObjectModel;
using System.IO;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CaptureFlow.App.ViewModels;

public partial class TemplateManagerViewModel : ObservableObject
{
    private readonly ITemplateRepository _templateRepository;
    private readonly ILogger<TemplateManagerViewModel> _logger;

    [ObservableProperty] private PageTemplate? _selectedPageTemplate;
    [ObservableProperty] private DocumentTemplate? _selectedDocumentTemplate;
    [ObservableProperty] private string _statusText = "";

    public ObservableCollection<PageTemplate> PageTemplates { get; } = [];
    public ObservableCollection<DocumentTemplate> DocumentTemplates { get; } = [];

    public TemplateManagerViewModel(ITemplateRepository templateRepository, ILogger<TemplateManagerViewModel> logger)
    {
        _templateRepository = templateRepository;
        _logger = logger;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var pageTemplates = await _templateRepository.GetAllPageTemplatesAsync();
            PageTemplates.Clear();
            foreach (var t in pageTemplates) PageTemplates.Add(t);

            var docTemplates = await _templateRepository.GetAllDocumentTemplatesAsync();
            DocumentTemplates.Clear();
            foreach (var t in docTemplates) DocumentTemplates.Add(t);

            StatusText = $"{PageTemplates.Count} page templates, {DocumentTemplates.Count} document templates";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load templates");
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeletePageTemplate()
    {
        if (SelectedPageTemplate == null) return;
        await _templateRepository.DeletePageTemplateAsync(SelectedPageTemplate.Id);
        await RefreshAsync();
        StatusText = "Template deleted";
    }

    [RelayCommand]
    private async Task DeleteDocumentTemplate()
    {
        if (SelectedDocumentTemplate == null) return;
        await _templateRepository.DeleteDocumentTemplateAsync(SelectedDocumentTemplate.Id);
        await RefreshAsync();
        StatusText = "Template deleted";
    }

    [RelayCommand]
    private async Task DuplicatePageTemplate()
    {
        if (SelectedPageTemplate == null) return;

        var copy = new PageTemplate
        {
            Name = SelectedPageTemplate.Name + " (copy)",
            ApplicableFileTypes = new(SelectedPageTemplate.ApplicableFileTypes),
            CaptureBoxes = SelectedPageTemplate.CaptureBoxes.Select(b => new CaptureBox
            {
                Name = b.Name,
                OutputHeader = b.OutputHeader,
                PageIndex = b.PageIndex,
                Rect = b.Rect,
                ExtractionMode = b.ExtractionMode,
                RowTargetMode = b.RowTargetMode,
                Enabled = b.Enabled
            }).ToList(),
            RepeatGroups = new(SelectedPageTemplate.RepeatGroups)
        };

        await _templateRepository.SavePageTemplateAsync(copy);
        await RefreshAsync();
        StatusText = "Template duplicated";
    }

    [RelayCommand]
    private void CreateDocumentTemplate()
    {
        var template = new DocumentTemplate { Name = "New Document Template" };
        DocumentTemplates.Add(template);
        SelectedDocumentTemplate = template;
    }

    [RelayCommand]
    private async Task ExportTemplate()
    {
        if (SelectedPageTemplate == null && SelectedDocumentTemplate == null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "JSON Files (*.json)|*.json",
            FileName = (SelectedPageTemplate?.Name ?? SelectedDocumentTemplate?.Name ?? "template") + ".json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            bool isDocTemplate = SelectedDocumentTemplate != null;
            string id = isDocTemplate ? SelectedDocumentTemplate!.Id : SelectedPageTemplate!.Id;
            var json = await _templateRepository.ExportTemplateAsync(id, isDocTemplate);
            await File.WriteAllTextAsync(dialog.FileName, json);
            StatusText = $"Exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportTemplate()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON Files (*.json)|*.json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            await _templateRepository.ImportTemplateAsync(json);
            await RefreshAsync();
            StatusText = "Template imported";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RenamePageTemplate(string newName)
    {
        if (SelectedPageTemplate == null || string.IsNullOrWhiteSpace(newName)) return;
        SelectedPageTemplate.Name = newName;
        SelectedPageTemplate.ModifiedUtc = DateTime.UtcNow;
        await _templateRepository.SavePageTemplateAsync(SelectedPageTemplate);
        await RefreshAsync();
    }
}
