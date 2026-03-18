using System.Windows;
using CaptureFlow.App.ViewModels;
using CaptureFlow.Core.Interfaces;
using CaptureFlow.Core.Services.Adapters;
using CaptureFlow.Core.Services.Extraction;
using CaptureFlow.Core.Services.Merge;
using CaptureFlow.Core.Services.OCR;
using CaptureFlow.Core.Services.Transforms;
using CaptureFlow.Core.Services.Validation;
using CaptureFlow.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaptureFlow.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
        });

        // Document adapters
        services.AddSingleton<IDocumentAdapter, PdfDocumentAdapter>();
        services.AddSingleton<IDocumentAdapter, DocxDocumentAdapter>();
        services.AddSingleton<IDocumentAdapter, PlainTextAdapter>();
        services.AddSingleton<IDocumentAdapter, EmailDocumentAdapter>();
        services.AddSingleton<IDocumentAdapter, ImageDocumentAdapter>();
        services.AddSingleton<DocumentAdapterFactory>();

        // Services
        services.AddSingleton<IOcrEngine, TesseractOcrEngine>();
        services.AddSingleton<TransformService>();
        services.AddSingleton<ValidationService>();
        services.AddSingleton<IExtractionService, ExtractionService>();
        services.AddSingleton<IBatchProcessor, BatchProcessor>();
        services.AddSingleton<CsvExportService>();

        // Merge services
        services.AddSingleton<DocxMergeService>();
        services.AddSingleton<PdfMergeService>();
        services.AddSingleton<IMergeService, MergeServiceRouter>();

        // Repositories
        services.AddSingleton<ITemplateRepository, JsonTemplateRepository>();
        services.AddSingleton<IProjectRepository, JsonProjectRepository>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<DocumentPreviewViewModel>();
        services.AddTransient<CaptureBoxViewModel>();
        services.AddTransient<ExtractionGridViewModel>();
        services.AddTransient<BatchProcessingViewModel>();
        services.AddTransient<MergeViewModel>();
        services.AddTransient<TemplateManagerViewModel>();
    }
}
