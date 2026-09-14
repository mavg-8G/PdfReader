using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PdfReader.Core;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PdfReader.Services;

/// <summary>Only bundled assets and the active read-only document may cross the WebView boundary.</summary>
public sealed class ViewerHost(WebView2 view) : IDisposable
{
    public const string Origin = "https://folio.local";
    // Folder-mapped hosts bypass WebResourceRequested. Document bytes must use an
    // unmapped origin so WebView2 invokes our in-process range handler.
    public const string DocumentOrigin = "https://folio-document.invalid";
    public string? DocumentUrl => Document is { } document ? $"{DocumentOrigin}/document/{document.Id}.pdf" : null;
    internal int DocumentRequestCount { get; private set; }
    internal int RangeRequestCount { get; private set; }
    public DocumentSession? Document { get; set; }
    public event Action<JsonElement>? Message;
    public event Action<string>? Failed;
    public event Action<string>? FileDropped;
    private bool _disposed;
    public async Task InitializeAsync()
    {
        await view.EnsureCoreWebView2Async();
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("folio.local", System.IO.Path.Combine(AppContext.BaseDirectory, "Viewer"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += ResourceRequested;
        core.WebMessageReceived += (_, e) =>
        {
            if (_disposed || e.Source != Origin + "/index.html" || e.WebMessageAsJson.Length > 512_000) return;
            try
            {
                using var json = JsonDocument.Parse(e.WebMessageAsJson);
                if (json.RootElement.TryGetProperty("type", out var kind) && kind.GetString() == "drop")
                {
                    if (e.AdditionalObjects.Count == 1 && e.AdditionalObjects[0] is CoreWebView2File file) FileDropped?.Invoke(file.Path);
                    return;
                }
                Message?.Invoke(json.RootElement.Clone());
            }
            catch (JsonException) { Failed?.Invoke("The document viewer sent an invalid response."); }
        };
        core.NavigationStarting += (_, e) => { if (e.Uri != Origin + "/index.html") e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += (_, _) => Failed?.Invoke("The document viewer stopped responding. Restart Folio and reopen your PDF.");
        core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) Failed?.Invoke("The document viewer could not start. Check that the bundled Viewer folder is installed."); };
        core.Navigate(Origin + "/index.html");
    }
    public void Send(object message)
    {
        if (!_disposed && view.CoreWebView2 is not null) view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }
    public void ShowPrintDialog() => view.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
    private void ResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)) { Deny(sender, args); return; }
        if (uri.Scheme is "data" or "blob") return;
        if (uri.Scheme == "https" && uri.Host == "folio.local" && uri.IsDefaultPort) return;
        if (uri.Scheme != "https" || uri.GetLeftPart(UriPartial.Authority) != DocumentOrigin) { Deny(sender, args); return; }
        var document = Document;
        if (document is null || uri.AbsolutePath != $"/document/{document.Id}.pdf") { Deny(sender, args); return; }
        const string cors = "Access-Control-Allow-Origin: " + Origin + "\r\nAccess-Control-Expose-Headers: Accept-Ranges, Content-Range, Content-Length\r\nVary: Origin";
        if (args.Request.Method == "OPTIONS")
        {
            args.Response = sender.Environment.CreateWebResourceResponse(null, 204, "No Content", cors + "\r\nAccess-Control-Allow-Methods: GET\r\nAccess-Control-Allow-Headers: Range\r\nCache-Control: no-store");
            return;
        }
        if (args.Request.Method != "GET") { Deny(sender, args); return; }
        DocumentRequestCount++;
        var rangeHeader = args.Request.Headers.Contains("Range") ? args.Request.Headers.GetHeader("Range") : null;
        if (!ByteRange.TryParse(rangeHeader, document.Length, out var range))
        {
            args.Response = sender.Environment.CreateWebResourceResponse(null, 416, "Range Not Satisfiable", $"Content-Range: bytes */{document.Length}\r\nCache-Control: no-store\r\n{cors}");
            return;
        }
        var partial = rangeHeader is not null;
        if (partial) RangeRequestCount++;
        var headers = $"Content-Type: application/pdf\r\nAccept-Ranges: bytes\r\nContent-Length: {range.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n{cors}";
        if (partial) headers += $"\r\nContent-Range: bytes {range.Start}-{range.End}/{document.Length}";
        args.Response = sender.Environment.CreateWebResourceResponse(document.CreateReadStream(range).AsRandomAccessStream(), partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
    }
    private static void Deny(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args) => args.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Cache-Control: no-store");
    public void Dispose() { _disposed = true; view.Close(); Document?.Dispose(); Document = null; }
}
