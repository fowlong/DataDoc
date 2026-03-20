using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CaptureFlow.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace CaptureFlow.App.Views;

public partial class CreatePanel : UserControl, IDesignerBridge
{
    private bool _isReady;
    private bool _initFailed;
    private bool _initStarted;
    private string? _initError;
    private TaskCompletionSource? _readyTcs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();

    public bool IsReady => _isReady;
    public event Action? OnReady;
    public event Action<int, int>? OnGenerationProgress;

    public CreatePanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Wire up the bridge (idempotent)
        if (DataContext is CreateViewModel vm)
            vm.DesignerBridge = this;

        // Only initialize WebView2 once
        if (_initStarted) return;
        _initStarted = true;

        await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            await DesignerWebView.EnsureCoreWebView2Async();

            // Map the app's output directory so WebView2 can load the HTML file
            var appDir = AppDomain.CurrentDomain.BaseDirectory;

            // Verify resources exist on disk
            var htmlPath = Path.Combine(appDir, "Resources", "pdfme-designer.html");
            var bundlePath = Path.Combine(appDir, "Resources", "pdfme-bundle.js");
            if (!File.Exists(htmlPath) || !File.Exists(bundlePath))
            {
                var missing = new System.Collections.Generic.List<string>();
                if (!File.Exists(htmlPath)) missing.Add("pdfme-designer.html");
                if (!File.Exists(bundlePath)) missing.Add("pdfme-bundle.js");
                if (DataContext is CreateViewModel vmRes)
                    vmRes.StatusText = $"Missing resource files in {Path.Combine(appDir, "Resources")}: {string.Join(", ", missing)}";
                return;
            }

            DesignerWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "captureflow.local",
                appDir,
                CoreWebView2HostResourceAccessKind.Allow);

            DesignerWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Navigate to the pdfme designer HTML
            DesignerWebView.CoreWebView2.Navigate("https://captureflow.local/Resources/pdfme-designer.html");
        }
        catch (Exception ex)
        {
            // WebView2 Runtime may not be installed
            if (DataContext is CreateViewModel vm)
                vm.StatusText = $"WebView2 error: {ex.Message}. Ensure WebView2 Runtime is installed.";
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "ready":
                    _isReady = true;
                    _readyTcs?.TrySetResult();
                    OnReady?.Invoke();
                    break;

                case "templateChanged":
                    if (DataContext is CreateViewModel vm)
                    {
                        if (root.TryGetProperty("fieldCount", out var fc))
                            vm.UpdateFieldCount(fc.GetInt32());

                        if (root.TryGetProperty("mergeFields", out var mf))
                        {
                            var fields = JsonSerializer.Deserialize<List<CreateViewModel.MergeFieldDto>>(
                                mf.GetRawText(),
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                            vm.UpdateMergeFields(fields);
                        }
                    }
                    break;

                case "generateResult":
                    HandleGenerateResult(root);
                    break;

                case "generateError":
                    HandleGenerateError(root);
                    break;

                case "basePdfLoaded":
                case "templateLoaded":
                    break;

                case "error":
                    var errorMsg = root.TryGetProperty("message", out var m) ? m.GetString() : "Unknown error";
                    _initFailed = true;
                    _initError = errorMsg;
                    // Fail the ready TCS so callers get the error instead of timing out
                    _readyTcs?.TrySetException(new InvalidOperationException($"Designer init failed: {errorMsg}"));
                    if (DataContext is CreateViewModel vm2)
                        vm2.StatusText = $"Designer error: {errorMsg}";
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebMessage parse error: {ex.Message}");
        }
    }

    private void HandleGenerateResult(JsonElement root)
    {
        if (!root.TryGetProperty("requestId", out var reqIdProp)) return;
        var requestId = reqIdProp.GetString();
        if (requestId == null) return;

        if (_pendingRequests.TryRemove(requestId, out var tcs))
        {
            var data = root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
            tcs.SetResult(data);
        }
    }

    private void HandleGenerateError(JsonElement root)
    {
        if (!root.TryGetProperty("requestId", out var reqIdProp)) return;
        var requestId = reqIdProp.GetString();
        if (requestId == null) return;

        if (_pendingRequests.TryRemove(requestId, out var tcs))
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown" : "Unknown";
            tcs.SetException(new InvalidOperationException($"PDF generation failed: {error}"));
        }
    }

    private static string EscapeForJs(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    private async Task WaitForReadyAsync(TimeSpan timeout)
    {
        if (_isReady) return;
        if (_initFailed)
            throw new InvalidOperationException($"Designer failed to initialize: {_initError}");
        var tcs = _readyTcs;
        if (tcs == null) return;
        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(tcs.Task, timeoutTask);
        if (completed == timeoutTask)
        {
            // Check if an error came in during the wait
            if (_initFailed)
                throw new InvalidOperationException($"Designer failed to initialize: {_initError}");

            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var bundleExists = File.Exists(Path.Combine(appDir, "Resources", "pdfme-bundle.js"));
            throw new TimeoutException(
                $"Designer did not become ready within {timeout.TotalSeconds}s. " +
                $"Bundle file exists: {bundleExists}. App dir: {appDir}");
        }
        // If the TCS was faulted (by an error message), this will re-throw
        await tcs.Task;
    }

    // ---- IDesignerBridge implementation ----

    public async Task LoadBasePdfAsync(string base64Pdf)
    {
        await WaitForReadyAsync(TimeSpan.FromSeconds(30));

        // For large PDFs, passing via script can exceed limits. Use a chunked approach if needed.
        // For now, pass directly (works for most documents).
        var escaped = EscapeForJs(base64Pdf);
        await DesignerWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.pdfmeApi.loadBasePdf('{escaped}')");
    }

    public async Task<string> GetTemplateJsonAsync()
    {
        if (!_isReady) return "{}";

        var result = await DesignerWebView.CoreWebView2.ExecuteScriptAsync(
            "window.pdfmeApi.getTemplate()");

        // ExecuteScriptAsync wraps string results in quotes — deserialize to unwrap
        return JsonSerializer.Deserialize<string>(result) ?? "{}";
    }

    public async Task SetTemplateJsonAsync(string json)
    {
        await WaitForReadyAsync(TimeSpan.FromSeconds(30));

        var escaped = EscapeForJs(json);
        await DesignerWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.pdfmeApi.setTemplate('{escaped}')");
    }

    public async Task<List<string>> GetFieldNamesAsync()
    {
        if (!_isReady) return [];

        var result = await DesignerWebView.CoreWebView2.ExecuteScriptAsync(
            "window.pdfmeApi.getFieldNames()");

        var jsonString = JsonSerializer.Deserialize<string>(result) ?? "[]";
        return JsonSerializer.Deserialize<List<string>>(jsonString) ?? [];
    }

    public async Task InsertMergeFieldAsync(string headerName)
    {
        await WaitForReadyAsync(TimeSpan.FromSeconds(30));
        var escaped = EscapeForJs(headerName);
        await DesignerWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.pdfmeApi.insertMergeField('{escaped}')");
    }

    public async Task ResetToBlankAsync()
    {
        await WaitForReadyAsync(TimeSpan.FromSeconds(30));
        await DesignerWebView.CoreWebView2.ExecuteScriptAsync(
            "window.pdfmeApi.resetToBlank()");
    }

    public async Task UpdateMergeFieldAsync(string fieldName, string propsJson)
    {
        await WaitForReadyAsync(TimeSpan.FromSeconds(30));
        var escaped = EscapeForJs(fieldName);
        var escapedProps = EscapeForJs(propsJson);
        await DesignerWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.pdfmeApi.updateMergeField('{escaped}', '{escapedProps}')");
    }

    public async Task<byte[]> GenerateSinglePdfAsync(string inputsJson)
    {
        await WaitForReadyAsync(TimeSpan.FromSeconds(30));

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>();
        _pendingRequests[requestId] = tcs;

        var escaped = EscapeForJs(inputsJson);
        await DesignerWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.pdfmeApi.generatePdf('{escaped}', '{requestId}')");

        // Wait for the JS to post back the result (with timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(requestId, out var pending))
                pending.TrySetException(new TimeoutException("PDF generation timed out"));
        });

        var base64 = await tcs.Task;
        return Convert.FromBase64String(base64);
    }
}
