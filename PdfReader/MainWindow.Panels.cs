using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PdfReader.Controls;
using PdfReader.Services;
using Windows.ApplicationModel.DataTransfer;

namespace PdfReader;

// The side panel beside the document hosts one tool at a time: scanned-page reading or annotations.
public sealed partial class MainWindow
{
    private void ShowPanel(FrameworkElement panel)
    {
        if (!_state.IsOpen) return;
        var annotations = ReferenceEquals(panel, AnnotationPanel);
        if (!annotations) DocumentView.SetTool(AnnotationTool.None);
        OcrPanel.Visibility = annotations ? Visibility.Collapsed : Visibility.Visible;
        AnnotationPanel.Visibility = annotations ? Visibility.Visible : Visibility.Collapsed;
        SidePanel.Visibility = Visibility.Visible;
        if (annotations) { UpdateAnnotationPanel(); NoteToolButton.Focus(FocusState.Programmatic); }
        else { UpdateOcrPanel(); OcrReadButton.Focus(FocusState.Programmatic); }
    }
    private void HidePanel()
    {
        if (SidePanel.Visibility == Visibility.Collapsed) return;
        _ocrCancel?.Cancel(); DocumentView.SetTool(AnnotationTool.None);
        SidePanel.Visibility = OcrPanel.Visibility = AnnotationPanel.Visibility = Visibility.Collapsed;
        ResetOcr();
        if (_state.IsOpen) DocumentView.Focus(FocusState.Programmatic);
    }
    private void ClosePanel_Click(object sender, RoutedEventArgs e) => HidePanel();

    private void ApplyPanelLanguage()
    {
        OcrHeading.Text = T("Read scanned page", "Leer página escaneada");
        OcrDescription.Text = T("Extract text from a photo or scan, one page at a time. Everything stays on this device.", "Extrae texto de una foto o escaneo, una página a la vez. Todo permanece en este dispositivo.");
        OcrLanguageBox.Header = T("Language", "Idioma del documento");
        OcrEnglishItem.Content = T("English", "Inglés");
        OcrCancelButton.Content = T("Cancel", "Cancelar");
        OcrText.Header = T("Recognized text", "Texto reconocido");
        OcrText.PlaceholderText = T("Text from this page will appear here.", "El texto de esta página aparecerá aquí.");
        OcrCopyButton.Content = T("Copy text", "Copiar texto");
        OcrNote.Text = T("OCR may make mistakes. This text is temporary; it isn’t added to the PDF or document-wide search.", "El OCR puede cometer errores. Este texto es temporal; no se agrega al PDF ni a la búsqueda del documento.");
        Tip(OcrCloseButton, "Close scanned-page reader", "Cerrar lector de páginas escaneadas");

        AnnotationHeading.Text = T("Annotations", "Anotaciones");
        AnnotationDescription.Text = T("Notes, highlights and drawings are saved in a separate .folio.json file beside the PDF.", "Las notas, resaltados y dibujos se guardan en un archivo .folio.json separado junto al PDF.");
        NoteToolButton.Content = T("Sticky note", "Nota adhesiva");
        DrawToolButton.Content = T("Draw", "Dibujar");
        NoteTextBox.Header = T("New note text", "Texto de la nota");
        NoteTextBox.PlaceholderText = T("Write a note, then click a page…", "Escribe una nota y luego haz clic en una página…");
        HighlightButton.Content = T("Highlight selection", "Resaltar selección");
        Tip(AnnotationCloseButton, "Close annotations", "Cerrar anotaciones");
        UpdateOcrPanel(); UpdateAnnotationPanel();
    }

    // ---- Annotations --------------------------------------------------------------------

    private void UpdateAnnotationPanel()
    {
        var tool = DocumentView.Tool;
        NoteToolButton.IsChecked = tool == AnnotationTool.Note; DrawToolButton.IsChecked = tool == AnnotationTool.Draw;
        AnnotationStatus.Text = tool switch
        {
            AnnotationTool.Note => T("Click a page to add your note.", "Haz clic en una página para agregar la nota."),
            AnnotationTool.Draw => T("Draw on a page. Select Draw again to stop.", "Dibuja sobre una página. Selecciona Dibujar otra vez para detenerte."),
            _ => T("Choose a tool. Select document text before highlighting it. Right-click a highlight or drawing to remove it.", "Elige una herramienta. Selecciona texto del documento antes de resaltarlo. Haz clic derecho en un resaltado o dibujo para quitarlo.")
        };
    }
    private void ToggleTool(AnnotationTool tool)
    {
        DocumentView.PendingNoteText = NoteTextBox.Text;
        DocumentView.SetTool(DocumentView.Tool == tool ? AnnotationTool.None : tool);
        // A ToggleButton flips itself on click; put both back in step with the viewer.
        UpdateAnnotationPanel();
    }
    private void NoteTool_Click(object sender, RoutedEventArgs e) => ToggleTool(AnnotationTool.Note);
    private void DrawTool_Click(object sender, RoutedEventArgs e) => ToggleTool(AnnotationTool.Draw);
    private void NoteText_Changed(object sender, TextChangedEventArgs e) => DocumentView.PendingNoteText = NoteTextBox.Text;
    private void Highlight_Click(object sender, RoutedEventArgs e) => _ = RunAsync(async () =>
    {
        AnnotationStatus.Text = await DocumentView.HighlightSelectionAsync()
            ? T("Changes are saved beside the PDF.", "Los cambios se guardan junto al PDF.")
            : T("Select some document text before highlighting it.", "Selecciona texto del documento antes de resaltarlo.");
    });

    // ---- Scanned-page reading -----------------------------------------------------------

    private void UpdateOcrPanel() => OcrReadButton.Content = T($"Read page {_state.Page}", $"Leer página {_state.Page}");
    private void ResetOcr()
    {
        _ocrCancel?.Cancel();
        OcrText.Text = ""; OcrCopyButton.IsEnabled = false; OcrProgress.Visibility = OcrCancelButton.Visibility = Visibility.Collapsed; OcrReadButton.IsEnabled = true;
        OcrStatus.Text = T("Choose the page language, then read the page.", "Elige el idioma de la página y luego léela.");
        UpdateOcrPanel();
    }
    private void OcrCancel_Click(object sender, RoutedEventArgs e) => _ocrCancel?.Cancel();
    private void OcrCopy_Click(object sender, RoutedEventArgs e)
    {
        if (OcrText.Text.Length == 0) return;
        var package = new DataPackage(); package.SetText(OcrText.SelectedText.Length > 0 ? OcrText.SelectedText : OcrText.Text);
        try { Clipboard.SetContent(package); OcrStatus.Text = T("Text copied.", "Texto copiado."); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { OcrStatus.Text = T("The clipboard is busy. Try copying again.", "El portapapeles está ocupado. Intenta copiar de nuevo."); }
    }
    private void OcrRead_Click(object sender, RoutedEventArgs e) => _ = RunAsync(async () =>
    {
        if (_pdf is not { } pdf || !_state.IsOpen || _busy || _printing) return;
        if (!pdf.CanCopy) { OcrStatus.Text = T("Text extraction is restricted by this document’s permissions.", "Los permisos del documento restringen la extracción de texto."); return; }
        var language = (OcrLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";
        var languageName = language == "es" ? T("Spanish", "español") : T("English", "inglés");
        int page = _state.Page, rotation = _state.Rotation;
        _ocrCancel?.Cancel(); var source = _ocrCancel = new CancellationTokenSource();
        OcrText.Text = ""; OcrCopyButton.IsEnabled = false; OcrReadButton.IsEnabled = false;
        OcrProgress.Visibility = OcrCancelButton.Visibility = Visibility.Visible;
        OcrStatus.Text = T($"Reading page {page}…", $"Leyendo la página {page}…");
        string? text = null; var failed = false;
        try { text = await PageOcr.RecognizeAsync(pdf, page, rotation, language, source.Token); }
        catch (OperationCanceledException) { if (ReferenceEquals(source, _ocrCancel)) { ResetOcr(); OcrStatus.Text = T("Reading cancelled. You can try again.", "Lectura cancelada. Puedes intentarlo de nuevo."); } return; }
        catch (Exception ex) when (ex is ObjectDisposedException or PdfReader.Core.DocumentException or System.Runtime.InteropServices.COMException) { failed = true; }
        // A newer request, a page change or a closed document has already reset the panel.
        if (!ReferenceEquals(source, _ocrCancel) || source.IsCancellationRequested) return;
        OcrProgress.Visibility = OcrCancelButton.Visibility = Visibility.Collapsed; OcrReadButton.IsEnabled = true;
        if (failed) OcrStatus.Text = T("Unable to read this page. Try another page or language.", "No se pudo leer esta página. Prueba con otra página u otro idioma.");
        else if (text is null) OcrStatus.Text = T($"Windows has no {languageName} text recognition installed. Add the language in Settings > Time & language > Language & region, then try again.", $"Windows no tiene instalado el reconocimiento de texto en {languageName}. Agrega el idioma en Configuración > Hora e idioma > Idioma y región e inténtalo de nuevo.");
        else if (text.Length == 0) OcrStatus.Text = T($"No text found on page {page}. Try another language or rotate the page upright.", $"No se encontró texto en la página {page}. Prueba con otro idioma o gira la página para que quede derecha.");
        else
        {
            OcrText.Text = text; OcrCopyButton.IsEnabled = true;
            OcrStatus.Text = T($"Page {page} · Check the recognized text against the image.", $"Página {page} · Compara el texto reconocido con la imagen.");
            AutomationProperties.SetName(OcrText, T("Recognized text", "Texto reconocido"));
        }
    });
}
