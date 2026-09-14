using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PdfReader;

public sealed partial class MainWindow
{
    private string T(string english, string spanish) => _language == "es" ? spanish : english;

    private void ApplyLanguage(string language)
    {
        _language = language == "es" ? "es" : "en";
        _settings.Data.Language = _language;
        Welcome.ApplyLanguage(_language);

        OpenButton.Label = T("Open PDF", "Abrir PDF");
        FindButton.Label = T("Search", "Buscar");
        OcrButton.Label = T("Read scanned page", "Leer página escaneada");
        RotateButton.Label = T("Rotate clockwise", "Girar a la derecha");
        SaveButton.Label = T("Save a copy", "Guardar una copia");
        PrintButton.Label = T("Print", "Imprimir");
        SettingsButton.Label = T("Settings", "Configuración");

        ScaleWidthItem.Text = FitWidthMenu.Label = T("Fit to width", "Ajustar al ancho");
        ScalePageItem.Text = FitPageMenu.Label = T("Fit to page", "Ajustar a la página");
        ActualSizeItem.Text = T("100% · Actual size", "100% · Tamaño real");
        FullscreenMenu.Label = T("Fullscreen · F11", "Pantalla completa · F11");
        PresentationMenu.Label = T("Presentation · F5", "Presentación · F5");
        HelpMenu.Label = T("Keyboard shortcuts", "Métodos abreviados de teclado");
        CloseMenu.Label = T("Close document · Ctrl+W", "Cerrar documento · Ctrl+W");

        SearchBox.PlaceholderText = T("Find in document…", "Buscar en el documento…");
        MatchCaseBox.Content = T("Match case", "Distinguir mayúsculas");
        SearchResultText.Text = T("Search selectable text in this PDF", "Busca texto seleccionable en este PDF");
        PagesHeading.Text = T("PAGES", "PÁGINAS");
        CancelBusyButton.Content = T("Cancel", "Cancelar");
        PrivacyText.Text = T("ON THIS DEVICE", "EN ESTE DISPOSITIVO");

        Tip(SidebarButton, "Page thumbnails", "Miniaturas de páginas", "Toggle page thumbnails", "Mostrar u ocultar miniaturas");
        Tip(PreviousButton, "Previous page (Page Up)", "Página anterior (Re Pág)", "Previous page", "Página anterior");
        Tip(PageBox, "Go to page (Ctrl+G)", "Ir a la página (Ctrl+G)", "Page number", "Número de página");
        Tip(NextButton, "Next page (Page Down)", "Página siguiente (Av Pág)", "Next page", "Página siguiente");
        Tip(ZoomOutButton, "Zoom out (Ctrl+-)", "Alejar (Ctrl+-)", "Zoom out", "Alejar");
        Tip(ZoomButton, "Zoom and page fitting", "Zoom y ajuste de página", "Zoom and fit options", "Opciones de zoom y ajuste");
        Tip(ZoomInButton, "Zoom in (Ctrl++)", "Acercar (Ctrl++)", "Zoom in", "Acercar");
        Tip(OpenButton, "Open PDF (Ctrl+O)", "Abrir PDF (Ctrl+O)");
        Tip(FindButton, "Search document (Ctrl+F)", "Buscar en el documento (Ctrl+F)");
        Tip(OcrButton, "Read text from the current scanned page (OCR)", "Leer texto de la página escaneada actual (OCR)");
        Tip(RotateButton, "Rotate clockwise (Ctrl+R)", "Girar a la derecha (Ctrl+R)");
        Tip(SaveButton, "Save a copy (Ctrl+S)", "Guardar una copia (Ctrl+S)");
        Tip(PrintButton, "Print (Ctrl+P)", "Imprimir (Ctrl+P)");
        Tip(SettingsButton, "Settings", "Configuración");
        Tip(FindPreviousButton, "Previous match (Shift+Enter)", "Coincidencia anterior (Mayús+Enter)", "Previous match", "Coincidencia anterior");
        Tip(FindNextButton, "Next match (Enter)", "Coincidencia siguiente (Enter)", "Next match", "Coincidencia siguiente");
        Tip(CloseSearchButton, "Close search", "Cerrar búsqueda");

        AutomationProperties.SetName(SearchBox, T("Search document text", "Buscar texto en el documento"));
        AutomationProperties.SetName(Thumbnails, T("Page thumbnails", "Miniaturas de páginas"));
        AutomationProperties.SetName(DocumentView, T("PDF document canvas", "Área del documento PDF"));
        AutomationProperties.SetName(LoadingBar, T("PDF loading progress", "Progreso de carga del PDF"));

        if (!_state.IsOpen)
        {
            FileNameText.Text = T("PDF reader", "Lector de PDF");
            if (!_busy) StatusText.Text = T("Ready", "Listo");
        }
        else if (!_busy)
        {
            UpdateDocumentStatus();
        }
        SendLanguage();
    }

    private void Tip(DependencyObject element, string english, string spanish, string? automationEnglish = null, string? automationSpanish = null)
    {
        ToolTipService.SetToolTip(element, T(english, spanish));
        AutomationProperties.SetName(element, T(automationEnglish ?? english, automationSpanish ?? spanish));
    }

    private void UpdateDocumentStatus()
    {
        if (!_state.IsOpen || _host.Document is not { } document) return;
        StatusText.Text = T(
            $"{_state.PageCount:N0} pages · {FormatSize(document.Length)} · Select text to copy",
            $"{_state.PageCount:N0} páginas · {FormatSize(document.Length)} · Selecciona texto para copiar");
    }

    private string TranslateKnown(string value)
    {
        if (_language != "es" || string.IsNullOrEmpty(value)) return value;
        if (value.StartsWith("Choose pages between 1 and ", StringComparison.Ordinal))
            return "Elige páginas entre 1 y " + value[27..];

        return value switch
        {
            "Choose a PDF file" => "Elige un archivo PDF",
            "Only files with a .pdf extension are supported." => "Solo se admiten archivos con la extensión .pdf.",
            "This file is empty" => "Este archivo está vacío",
            "Choose a PDF that contains document data." => "Elige un PDF que contenga datos de documento.",
            "This PDF is too large" => "Este PDF es demasiado grande",
            "The safe opening limit is 512 MB. Split the document into smaller PDFs and try again." => "El límite seguro de apertura es 512 MB. Divide el documento en PDF más pequeños e inténtalo de nuevo.",
            "This is not a valid PDF" => "Este no es un PDF válido",
            "The file does not have a PDF header. It may be damaged or have the wrong extension." => "El archivo no tiene un encabezado PDF. Puede estar dañado o tener una extensión incorrecta.",
            "Choose a different filename" => "Elige otro nombre de archivo",
            "Save a copy preserves your original PDF. Choose another name or folder." => "Guardar una copia conserva el PDF original. Elige otro nombre o carpeta.",
            "Use the .pdf extension" => "Usa la extensión .pdf",
            "Save the copy with a filename ending in .pdf." => "Guarda la copia con un nombre que termine en .pdf.",
            "Reading preferences could not be restored. New preferences will be saved for this session." => "No se pudieron restaurar las preferencias de lectura. Se guardarán preferencias nuevas para esta sesión.",
            "Reading preferences could not be saved. Check available space and folder permissions." => "No se pudieron guardar las preferencias de lectura. Comprueba el espacio disponible y los permisos de la carpeta.",
            "The document viewer sent an invalid response." => "El visor de documentos envió una respuesta no válida.",
            "The document viewer stopped responding. Restart Folio and reopen your PDF." => "El visor de documentos dejó de responder. Reinicia Folio y vuelve a abrir el PDF.",
            "The document viewer could not start. Check that the bundled Viewer folder is installed." => "El visor de documentos no pudo iniciarse. Comprueba que la carpeta Viewer incluida esté instalada.",
            "The local document connection failed" => "Falló la conexión local con el documento",
            "Close and reopen Folio, then try the PDF again. If the problem continues, install the latest Folio build." => "Cierra y vuelve a abrir Folio; luego prueba el PDF otra vez. Si el problema continúa, instala la versión más reciente de Folio.",
            "Unable to unlock this PDF" => "No se pudo desbloquear este PDF",
            "The password was not accepted. Open the document again to retry." => "No se aceptó la contraseña. Vuelve a abrir el documento para intentarlo otra vez.",
            "This PDF could not be read" => "No se pudo leer este PDF",
            "The file appears damaged or incomplete. Try obtaining a new copy." => "El archivo parece estar dañado o incompleto. Intenta obtener una copia nueva.",
            "The PDF is no longer available" => "El PDF ya no está disponible",
            "The file could not be read. Open it again from your device." => "No se pudo leer el archivo. Vuelve a abrirlo desde tu dispositivo.",
            "Unable to open this PDF" => "No se pudo abrir este PDF",
            "This PDF uses dynamic XFA forms, which are not supported. Open it in a reader with XFA support." => "Este PDF usa formularios XFA dinámicos, que no son compatibles. Ábrelo en un lector compatible con XFA.",
            "A page could not be rendered completely. The PDF may contain damaged or unsupported content." => "No se pudo mostrar una página por completo. El PDF puede contener contenido dañado o no compatible.",
            "Printing is restricted by this document’s permissions." => "Los permisos de este documento restringen la impresión.",
            "This print batch exceeds the memory limit. Choose fewer pages and try again." => "Este lote de impresión supera el límite de memoria. Elige menos páginas e inténtalo de nuevo.",
            "Unable to prepare this page for printing." => "No se pudo preparar esta página para imprimir.",
            "Drop one PDF file at a time, or use Open PDF." => "Arrastra un solo archivo PDF a la vez o usa Abrir PDF.",
            "Use page numbers or ranges, for example 1-5, 8." => "Usa números o intervalos de páginas, por ejemplo 1-5, 8.",
            "Enter a valid last page." => "Introduce una última página válida.",
            "Print up to 100 pages per batch." => "Imprime hasta 100 páginas por lote.",
            "Enter at least one page to print." => "Introduce al menos una página para imprimir.",
            _ => value
        };
    }
}
