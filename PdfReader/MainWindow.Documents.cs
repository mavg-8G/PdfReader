using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfReader.Controls;
using PdfReader.Core;
using PdfReader.Services;
using PdfReader.ViewModels;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.Pickers;

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
        _busy = true; MessageBar.IsOpen = false;
        _opening?.Dispose(); _opening = new CancellationTokenSource();
        var cancellationToken = _opening.Token;
        DocumentSession? document = null; PdfDocument? pdf = null;
        try
        {
            document = await DocumentSession.OpenAsync(path, cancellationToken);
            SetBusy(T("Opening your document…", "Abriendo el documento…"));
            string? password = null;
            while (pdf is null)
            {
                // Parsing and measuring a large document can take a moment, so it stays off the UI thread.
                try { var session = document; var attempt = password; pdf = await Task.Run(() => PdfDocument.Open(session, attempt)); }
                catch (PdfPasswordException ex)
                {
                    BusyOverlay.Visibility = Visibility.Collapsed;
                    password = await PasswordAsync(ex.Incorrect);
                    if (password is null || cancellationToken.IsCancellationRequested) return;
                    BusyOverlay.Visibility = Visibility.Visible;
                }
            }
            var markupStore = new DocumentMarkupStore(document);
            var (markup, markupError) = await markupStore.LoadAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || _closed) return;

            // Closing the viewer first flushes the previous document's pending markup to its own store.
            Remember(); ReleaseDocument();
            _document = document; _pdf = pdf; _markupStore = markupStore; document = null; pdf = null;
            _state.Open(_pdf.PageCount); _canPrint = _pdf.CanPrint && PrintService.IsSupported;
            for (var page = 1; page <= _state.PageCount; page++) _thumbnails.Add(new(page));
            Thumbnails.ItemsSource = _thumbnails;
            SearchPanel.Visibility = Visibility.Collapsed; SearchBox.Text = "";
            AppTitleBar.Subtitle = _document.Name; AppTitleBar.IsBackButtonVisible = true;
            Title = _document.Name + " — Folio"; ToolbarArea.Visibility = Visibility.Visible; DocumentSplit.Visibility = Visibility.Visible; Welcome.Visibility = Visibility.Collapsed;
            UpdateSidebar();
            var recent = _settings.Find(_document.Key);
            DocumentView.Open(_pdf, markup, recent?.Page ?? 1, recent?.Scale ?? "page-width", recent?.Rotation ?? 0);
            EndBusy(); UpdateControls(); UpdateDocumentStatus(); Remember();
            DocumentView.Focus(FocusState.Programmatic);
            if (markupError is not null) ShowWarning(T("Annotations could not be loaded. The PDF was opened without them.", "No se pudieron cargar las anotaciones. El PDF se abrió sin ellas."));
        }
        finally
        {
            // Anything still held here was never handed to the window: a cancelled or failed open.
            pdf?.Dispose(); document?.Dispose();
            if (_busy && !_printing) EndBusy();
        }
    }
    private void OnViewerState(ViewerState state)
    {
        if (!_state.IsOpen) return;
        var page = _state.Page; var rotation = _state.Rotation;
        _state.Update(state.Page, state.Zoom, state.Scale, state.Rotation);
        UpdateControls(); _syncing = true; Thumbnails.SelectedIndex = _state.Page - 1;
        if (DocumentSplit.IsPaneOpen && _thumbnails.Count >= _state.Page) Thumbnails.ScrollIntoView(_thumbnails[_state.Page - 1]);
        _syncing = false; Remember();
        // Recognized text belongs to one page in one orientation.
        if (page != _state.Page || rotation != _state.Rotation) ResetOcr();
    }
    private void OnSearchStatus(SearchStatus status)
    {
        SearchResultText.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? T("Search selectable text in this PDF", "Busca texto seleccionable en este PDF")
            : status.Total == 0
                ? status.Pending ? T("Searching…", "Buscando…") : T("No matches · scanned pages may need OCR", "Sin coincidencias · las páginas escaneadas pueden necesitar OCR")
                : status.Pending
                    ? T($"{status.Current} of {status.Total}+ matches · searching…", $"{status.Current} de {status.Total}+ coincidencias · buscando…")
                    : T($"{status.Current} of {status.Total} matches", $"{status.Current} de {status.Total} coincidencias");
    }
    /// <summary>Asks for the document password. Returns null when the reader cancels.</summary>
    private async Task<string?> PasswordAsync(bool incorrect)
    {
        var input = new PasswordBox { PlaceholderText = T("Document password", "Contraseña del documento"), MinWidth = 260 };
        AutomationProperties.SetName(input, T("Document password", "Contraseña del documento"));
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = incorrect ? T("That password wasn’t accepted. Please try again.", "No se aceptó esa contraseña. Inténtalo de nuevo.") : T("This document is encrypted. Enter its password to read it.", "Este documento está cifrado. Introduce su contraseña para leerlo."), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input); content.Children.Add(new TextBlock { Text = T("Your password is never saved.", "La contraseña nunca se guarda."), FontSize = 12, Opacity = .7 });
        var dialog = NewDialog(T("Unlock PDF", "Desbloquear PDF"), content, T("Unlock", "Desbloquear"), T("Cancel", "Cancelar"));
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = input.Password.Length == 0;
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        var result = await ShowDialogAsync(dialog);
        var password = result == ContentDialogResult.Primary ? input.Password : null;
        input.Password = "";
        return password;
    }
    // Thumbnails render one at a time, newest request first, so fast scrolling never floods the PDF engine.
    private async void PumpThumbnails()
    {
        if (_thumbnailPump) return;
        _thumbnailPump = true;
        try
        {
            while (_thumbnailQueue.Count > 0 && _document is { } document)
            {
                var item = _thumbnailQueue[^1]; _thumbnailQueue.RemoveAt(_thumbnailQueue.Count - 1);
                var bitmap = await DocumentView.RenderThumbnailAsync(item.Page);
                if (!ReferenceEquals(document, _document)) break;
                if (bitmap is null) { item.Requested = false; continue; }
                item.Image = bitmap;
                _thumbnailCache.Remove(item.Page); _thumbnailCache.AddLast(item.Page);
                while (_thumbnailCache.Count > 40)
                {
                    var old = _thumbnailCache.First!.Value; _thumbnailCache.RemoveFirst();
                    _thumbnails[old - 1].Image = null; _thumbnails[old - 1].Requested = false;
                }
            }
        }
        finally { _thumbnailPump = false; }
    }
    private async Task SaveMarkupAsync()
    {
        if (_document is not { } document || _markupStore is not { } store) return;
        // The snapshot is taken now; the reader may keep annotating while the file is written.
        var error = await store.SaveAsync(DocumentView.SnapshotMarkup());
        if (ReferenceEquals(document, _document) && error is not null) ShowWarning(TranslateKnown(error));
    }
    private async Task SaveCopyAsync()
    {
        if (_document is not { } document || !_state.IsOpen || _busy || _printing || _dialogOpen) return;
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(document.Name) + T(" - copy", " - copia") };
        picker.FileTypeChoices.Add(T("PDF document", "Documento PDF"), new List<string> { ".pdf" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        _dialogOpen = true; StorageFile? destination;
        try { destination = await picker.PickSaveFileAsync(); } finally { _dialogOpen = false; }
        if (destination is null) return;
        _busy = true;
        try { await document.SaveCopyAsync(destination.Path); StatusText.Text = T("Copy saved · ", "Copia guardada · ") + destination.Name; }
        finally { _busy = false; }
    }
    private async Task PrintAsync()
    {
        if (_pdf is not { } pdf || _document is not { } document || !_state.IsOpen || _busy || _printing || _dialogOpen) return;
        if (!pdf.CanPrint) { ShowWarning(T("Printing is restricted by this document’s permissions.", "Los permisos de este documento restringen la impresión.")); return; }
        if (!_canPrint) { ShowWarning(T("Printing is not available on this device.", "La impresión no está disponible en este dispositivo.")); return; }
        var pages = new TextBox { Text = _state.PageCount <= 30 ? $"1-{_state.PageCount}" : _state.Page.ToString(), PlaceholderText = "1-5, 8", Header = T("Pages to print", "Páginas para imprimir") };
        var hint = new TextBlock { Text = T("Print the selected pages at 144 dpi. Choose paper size and orientation in the next dialog. Large documents can be printed in smaller batches.", "Imprime las páginas seleccionadas a 144 ppp. Elige el tamaño y la orientación del papel en el siguiente diálogo. Los documentos grandes pueden imprimirse en lotes más pequeños."), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 14 }; panel.Children.Add(hint); panel.Children.Add(pages); panel.Children.Add(validation);
        var dialog = NewDialog(T("Print document", "Imprimir documento"), panel, T("Continue", "Continuar"), T("Cancel", "Cancelar")); int[]? selected = null;
        dialog.PrimaryButtonClick += (_, args) => { try { selected = PrintRange.Parse(pages.Text, _state.PageCount); } catch (FormatException ex) { validation.Text = TranslateKnown(ex.Message); args.Cancel = true; } };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || selected is null) return;

        HidePanel();
        _printing = true; SetBusy(T("Preparing pages for printing…", "Preparando las páginas para imprimir…"));
        _printCancel?.Dispose(); _printCancel = new CancellationTokenSource(); var cancel = _printCancel.Token;
        try
        {
            var rotation = _state.Rotation; var images = new List<ImageSource>(); long total = 0;
            for (var index = 0; index < selected.Length; index++)
            {
                var size = pdf.GetPageSize(selected[index]);
                // Two device pixels per point is 144 dpi.
                int width = (int)Math.Ceiling((rotation is 90 or 270 ? size.Height : size.Width) * 2), height = (int)Math.Ceiling((rotation is 90 or 270 ? size.Width : size.Height) * 2);
                total += (long)width * height;
                if ((long)width * height > 16_000_000 || total > 80_000_000) throw new DocumentException("Unable to prepare printing", "This print batch exceeds the memory limit. Choose fewer pages and try again.");
                var page = selected[index];
                var pixels = await Task.Run(() => { var buffer = new byte[width * height * 4]; pdf.Render(page, rotation, width, height, buffer, forPrinting: true); return buffer; }, cancel);
                if (cancel.IsCancellationRequested || !ReferenceEquals(pdf, _pdf)) return;
                var bitmap = new WriteableBitmap(width, height);
                using (var target = bitmap.PixelBuffer.AsStream()) target.Write(pixels, 0, pixels.Length);
                images.Add(bitmap);
                BusyText.Text = T($"Preparing page {index + 1} of {selected.Length}…", $"Preparando la página {index + 1} de {selected.Length}…");
            }
            EndBusy(); StatusText.Text = T("Print preview is ready. Choose your printer and paper settings.", "La vista previa está lista. Elige la impresora y la configuración del papel.");
            var submitted = await _print.PrintAsync(document.Name, images);
            if (ReferenceEquals(document, _document)) StatusText.Text = submitted ? T("Sent to the printer", "Enviado a la impresora") : T("Print dialog closed", "Diálogo de impresión cerrado");
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError(T("Unable to prepare printing", "No se pudo preparar la impresión"), TranslateKnown(ex.Message));
        }
        finally { _printing = false; EndBusy(); }
    }
    // Detaches the viewer and frees the current document without touching the surrounding UI.
    private void ReleaseDocument()
    {
        _printCancel?.Cancel(); HidePanel();
        DocumentView.Close();
        _pdf?.Dispose(); _pdf = null; _document?.Dispose(); _document = null; _markupStore = null;
        _state.Reset(); _canPrint = false;
        _thumbnails.Clear(); _thumbnailQueue.Clear(); _thumbnailCache.Clear(); Thumbnails.ItemsSource = null;
    }
    private void CloseDocument()
    {
        _activeDialog?.Hide();
        _opening?.Cancel(); Remember();
        if (_presentation) SetPresentation(false);
        ReleaseDocument(); _printing = false;
        DocumentSplit.IsPaneOpen = false; DocumentSplit.Visibility = Visibility.Collapsed; ToolbarArea.Visibility = Visibility.Collapsed;
        Welcome.SetRecent(_settings.Data.Recent); Welcome.Visibility = Visibility.Visible;
        AppTitleBar.Subtitle = T("PDF reader", "Lector de PDF"); AppTitleBar.IsBackButtonVisible = false; Title = "Folio";
        SearchPanel.Visibility = Visibility.Collapsed;
        EndBusy(); UpdateControls(); StatusText.Text = T("Ready", "Listo");
    }
    private static string FormatSize(long length) => length < 1024 * 1024 ? $"{length / 1024.0:0.#} KB" : $"{length / 1024.0 / 1024:0.#} MB";
}
