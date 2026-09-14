using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfReader.Core;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace PdfReader;
public sealed partial class MainWindow
{
    private async Task OpenPickerAsync()
    {
        if (_busy || _printing || _dialogOpen) return;
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".pdf");
        _dialogOpen = true;
        StorageFile? file;
        try { file = await picker.PickSingleFileAsync(); } finally { _dialogOpen = false; }
        if (file is not null) await OpenDocumentAsync(file.Path);
    }
    private async Task OpenDocumentAsync(string path)
    {
        if (_closed || _busy || _printing || _dialogOpen) return;
        if (!_viewerReady.Task.IsCompletedSuccessfully) { _pendingPath = path; StatusText.Text = "Starting the document viewer…"; return; }
        _busy = true; MessageBar.IsOpen = false;
        _opening?.Dispose(); _opening = new CancellationTokenSource();
        var cancellationToken = _opening.Token;
        try
        {
            var document = await DocumentSession.OpenAsync(path, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _closed) { document.Dispose(); return; }
            Remember();
            var previous = _host.Document;
            _host.Document = document; _state.Reset(); _canPrint = false;
            _thumbnails.Clear(); _thumbnailCache.Clear(); Thumbnails.ItemsSource = null;
            SearchPanel.Visibility = Visibility.Collapsed; SearchBox.Text = "";
            FileNameText.Text = document.Name; ToolTipService.SetToolTip(FileNameText, document.Path);
            Title = document.Name + " — Folio"; Welcome.Visibility = Visibility.Collapsed;
            DocumentView.IsTabStop = true; SetBusy("Opening your document…"); UpdateControls();
            var recent = _settings.Find(document.Key);
            _host.Send(new { type = "open", id = document.Id, url = _host.DocumentUrl, restore = new { page = recent?.Page ?? 1, scale = recent?.Scale ?? "page-width", rotation = recent?.Rotation ?? 0 } });
            previous?.Dispose(); _openTimer.Start();
        }
        catch { EndBusy(); throw; }
    }
    private async Task ReceiveAsync(JsonElement message)
    {
        var type = GetString(message, "type");
        if (type == "ready") { _viewerReady.TrySetResult(); return; }
        if (type == "shortcut") { await ActionAsync(GetString(message, "action")); return; }
        var id = GetString(message, "id");
        if (id != _host.Document?.Id) return;
        switch (type)
        {
            case "loaded":
                _state.Open(message.GetProperty("pages").GetInt32());
                _canPrint = message.GetProperty("canPrint").GetBoolean();
                for (var page = 1; page <= _state.PageCount; page++) _thumbnails.Add(new(page));
                Thumbnails.ItemsSource = _thumbnails; UpdateControls(); UpdateSidebar();
                StatusText.Text = $"{_state.PageCount:N0} pages · {FormatSize(_host.Document!.Length)} · Select text to copy";
                Remember(); break;
            case "state":
                if (!_state.IsOpen) return;
                if (_busy && !_printing) { _openTimer.Stop(); EndBusy(); }
                _state.Update(message.GetProperty("page").GetInt32(), message.GetProperty("zoom").GetDouble(), GetString(message, "scale"), message.GetProperty("rotation").GetInt32());
                UpdateControls(); _syncing = true; Thumbnails.SelectedIndex = _state.Page - 1;
                if (DocumentSplit.IsPaneOpen && _thumbnails.Count >= _state.Page) Thumbnails.ScrollIntoView(_thumbnails[_state.Page - 1]);
                _syncing = false; Remember(); break;
            case "progress":
                if (!_busy) break;
                var loaded = message.GetProperty("loaded").GetDouble();
                var total = message.TryGetProperty("total", out var t) && t.TryGetDouble(out var totalValue) ? totalValue : 0;
                LoadingBar.IsIndeterminate = total <= 0;
                if (total > 0) LoadingBar.Value = Math.Clamp(loaded / total * 100, 0, 100);
                BusyText.Text = total > 0 ? $"Opening your document… {Math.Clamp(loaded / total * 100, 0, 100):0}%" : "Reading your document…";
                break;
            case "password": await PasswordAsync(message.GetProperty("incorrect").GetBoolean(), id); break;
            case "error":
                var title = GetString(message, "title"); var error = GetString(message, "message");
                CloseDocument(); ShowError(title, error); break;
            case "warning": ShowWarning(GetString(message, "message")); break;
            case "find":
                var current = message.TryGetProperty("current", out var c) ? c.GetInt32() : 0;
                var count = message.TryGetProperty("total", out var n) ? n.GetInt32() : 0;
                var pending = message.TryGetProperty("pending", out var p) && p.GetBoolean();
                SearchResultText.Text = string.IsNullOrEmpty(SearchBox.Text) ? "Search selectable text in this PDF" : pending ? "Searching…" : count == 0 ? "No matches · scanned pages may need OCR" : $"{current} of {count} matches";
                break;
            case "thumbnail": await SetThumbnailAsync(message, id); break;
            case "thumbnailFailed":
                var failedPage = message.GetProperty("page").GetInt32();
                if (failedPage >= 1 && failedPage <= _thumbnails.Count) _thumbnails[failedPage - 1].Requested = false;
                break;
            case "printProgress": BusyText.Text = $"Preparing page {message.GetProperty("current").GetInt32()} of {message.GetProperty("total").GetInt32()}…"; break;
            case "printReady":
                EndBusy(); StatusText.Text = "Print preview is ready. Choose your printer and paper settings.";
                _host.ShowPrintDialog(); break;
            case "printError": _printing = false; EndBusy(); ShowError("Unable to prepare printing", GetString(message, "message")); break;
            case "printClosed": _printing = false; EndBusy(); StatusText.Text = "Print dialog closed"; break;
        }
    }
    private async Task PasswordAsync(bool incorrect, string id)
    {
        _openTimer.Stop();
        var input = new PasswordBox { PlaceholderText = "Document password", MinWidth = 260 };
        AutomationProperties.SetName(input, "Document password");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = incorrect ? "That password wasn’t accepted. Please try again." : "This document is encrypted. Enter its password to read it.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input); content.Children.Add(new TextBlock { Text = "Your password is never saved.", FontSize = 12 });
        var dialog = NewDialog("Unlock PDF", content, "Unlock", "Cancel");
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = input.Password.Length == 0;
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        var result = await ShowDialogAsync(dialog);
        if (_host.Document?.Id != id) return;
        if (result == ContentDialogResult.Primary) { Send("password", value: input.Password); input.Password = ""; _openTimer.Start(); }
        else CloseDocument();
    }
    private async Task SetThumbnailAsync(JsonElement message, string id)
    {
        var page = message.GetProperty("page").GetInt32();
        if (page < 1 || page > _thumbnails.Count) return;
        var bytes = Convert.FromBase64String(GetString(message, "data"));
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
        var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
        if (id != _host.Document?.Id || page > _thumbnails.Count) return;
        _thumbnails[page - 1].Image = bitmap;
        _thumbnailCache.Remove(page); _thumbnailCache.AddLast(page);
        while (_thumbnailCache.Count > 40)
        {
            var old = _thumbnailCache.First!.Value; _thumbnailCache.RemoveFirst();
            _thumbnails[old - 1].Image = null; _thumbnails[old - 1].Requested = false;
        }
    }
    private async Task SaveCopyAsync()
    {
        if (_host.Document is not { } document || !_state.IsOpen || _busy || _printing || _dialogOpen) return;
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(document.Name) + " - copy" };
        picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        _dialogOpen = true; StorageFile? destination;
        try { destination = await picker.PickSaveFileAsync(); } finally { _dialogOpen = false; }
        if (destination is null) return;
        _busy = true;
        try { await document.SaveCopyAsync(destination.Path); StatusText.Text = "Copy saved · " + destination.Name; }
        finally { _busy = false; }
    }
    private async Task PrintAsync()
    {
        if (!_state.IsOpen || _busy || _printing || _dialogOpen) return;
        if (!_canPrint) { ShowWarning("Printing is restricted by this document’s permissions."); return; }
        var pages = new TextBox { Text = _state.PageCount <= 30 ? $"1-{_state.PageCount}" : _state.Page.ToString(), PlaceholderText = "1-5, 8", Header = "Pages to print" };
        var hint = new TextBlock { Text = "Print the selected pages at 144 dpi. Choose paper size and orientation in the next dialog. Large documents can be printed in smaller batches.", TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 14 }; panel.Children.Add(hint); panel.Children.Add(pages); panel.Children.Add(validation);
        var dialog = NewDialog("Print document", panel, "Continue", "Cancel"); int[]? selected = null;
        dialog.PrimaryButtonClick += (_, args) => { try { selected = PrintRange.Parse(pages.Text, _state.PageCount); } catch (FormatException ex) { validation.Text = ex.Message; args.Cancel = true; } };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || selected is null) return;
        _printing = true; SetBusy("Preparing pages for printing…");
        _host.Send(new { type = "print", id = _host.Document!.Id, pages = selected });
    }
    private void CloseDocument()
    {
        _activeDialog?.Hide();
        if (_printing) { Send("cancelPrint"); _printing = false; }
        _opening?.Cancel(); _openTimer.Stop(); Remember();
        if (_presentation) SetPresentation(false);
        _host.Send(new { type = "close" });
        var previous = _host.Document; _host.Document = null; previous?.Dispose();
        _state.Reset(); _thumbnails.Clear(); _thumbnailCache.Clear(); Thumbnails.ItemsSource = null;
        DocumentSplit.IsPaneOpen = false;
        Welcome.SetRecent(_settings.Data.Recent); Welcome.Visibility = Visibility.Visible; DocumentView.IsTabStop = false;
        FileNameText.Text = "A quieter place for your PDFs"; Title = "Folio"; ToolTipService.SetToolTip(FileNameText, null);
        SearchPanel.Visibility = Visibility.Collapsed;
        EndBusy(); UpdateControls(); StatusText.Text = "Ready when you are";
    }
    private static string GetString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string FormatSize(long length) => length < 1024 * 1024 ? $"{length / 1024.0:0.#} KB" : $"{length / 1024.0 / 1024:0.#} MB";
}
