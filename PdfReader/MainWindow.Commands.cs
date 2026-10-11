using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PdfReader.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace PdfReader;

public sealed partial class MainWindow
{
    private void Navigate(int page) { if (_state.IsOpen && !_busy) DocumentView.GoToPage(_state.Navigate(page)); }
    private void Zoom(double factor) { if (_state.IsOpen && !_busy) DocumentView.SetScale(Math.Clamp(_state.Zoom * factor, .25, 5).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    private void Fit(string scale) { if (_state.IsOpen) DocumentView.SetScale(scale); }
    private void SetBusy(string message) { _busy = true; BusyText.Text = message; BusyOverlay.Visibility = Visibility.Visible; LoadingBar.Visibility = Visibility.Visible; LoadingBar.IsIndeterminate = true; }
    private void EndBusy() { _busy = false; BusyOverlay.Visibility = Visibility.Collapsed; LoadingBar.Visibility = Visibility.Collapsed; }
    private void Remember()
    {
        if (_document is { } document && _state.IsOpen) { _settings.Remember(document, _state); _saveTimer.Stop(); _saveTimer.Start(); }
    }
    private async Task PersistAsync()
    {
        try
        {
            if (_document is { } document && _state.IsOpen) _settings.Remember(document, _state);
            if (!await _settings.SaveAsync() && !_closed) ShowWarning(_settings.LastError!);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private void UpdateControls()
    {
        NavigationControls.IsEnabled = FindButton.IsEnabled = AnnotateButton.IsEnabled = OcrButton.IsEnabled = RotateButton.IsEnabled = SaveButton.IsEnabled = _state.IsOpen;
        PrintButton.IsEnabled = _state.IsOpen && _canPrint;
        PreviousButton.IsEnabled = _state.Page > 1; NextButton.IsEnabled = _state.Page < _state.PageCount;
        ZoomOutButton.IsEnabled = _state.Zoom > .25; ZoomInButton.IsEnabled = _state.Zoom < 5;
        PageBox.Text = _state.Page.ToString(); PageCountText.Text = $"/ {_state.PageCount}";
        ZoomButton.Content = $"{_state.Zoom * 100:0}%";
    }
    private void ShowError(string title, string message) { MessageBar.Title = title; MessageBar.Message = message; MessageBar.Severity = InfoBarSeverity.Error; MessageBar.IsOpen = true; }
    private void ShowWarning(string message) { MessageBar.Title = T("Please note", "Ten en cuenta"); MessageBar.Message = message; MessageBar.Severity = InfoBarSeverity.Warning; MessageBar.IsOpen = true; }
    private void ShowSearch()
    {
        if (!_state.IsOpen || _busy) return;
        SearchPanel.Visibility = Visibility.Visible; SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll();
    }
    private void Find(bool again, bool previous)
    {
        if (!_state.IsOpen) return;
        DocumentView.Find(SearchBox.Text, MatchCaseBox.IsChecked == true, again, previous);
    }
    private void CloseSearch() { SearchPanel.Visibility = Visibility.Collapsed; _searchTimer.Stop(); DocumentView.CloseFind(); DocumentView.Focus(FocusState.Programmatic); }
    private void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        _settings.Data.Theme = theme;
    }
    private void UpdateSidebar()
    {
        DocumentSplit.DisplayMode = Root.ActualWidth < 900 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        DocumentSplit.IsPaneOpen = _state.IsOpen && _sidebarWanted && Root.ActualWidth >= 900 && !_presentation;
    }
    private void SetFullscreen(bool enabled)
    {
        _fullscreen = enabled; AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        ApplyMinimumSize();
    }
    // Below this size the toolbar and side panels can no longer lay out usefully.
    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.PreferredMinimumWidth = 480; presenter.PreferredMinimumHeight = 420; }
    }
    private void SetPresentation(bool enabled)
    {
        if (enabled && (!_state.IsOpen || _busy)) return;
        if (enabled && SearchPanel.Visibility == Visibility.Visible) CloseSearch();
        _presentation = enabled; if (enabled) HidePanel(); DocumentView.SetPresentation(enabled); SetFullscreen(enabled);
        ToolbarArea.Visibility = enabled ? Visibility.Collapsed : _state.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.Visibility = Notices.Visibility = StatusBar.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        UpdateSidebar();
    }
    private ContentDialog NewDialog(string title, object content, string primary, string close) => new()
    { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = close, DefaultButton = ContentDialogButton.Primary };
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen || _closed) return ContentDialogResult.None;
        _dialogOpen = true; _activeDialog = dialog;
        try { return await dialog.ShowAsync(); } finally { _dialogOpen = false; _activeDialog = null; }
    }
    private async Task SettingsAsync()
    {
        var theme = new ComboBox
        {
            ItemsSource = new[] { T("Use system setting", "Usar la configuración del sistema"), T("Light", "Claro"), T("Dark", "Oscuro") },
            SelectedIndex = _settings.Data.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 },
            MinWidth = 280,
            Header = T("App theme", "Tema de la aplicación")
        };
        var language = new ComboBox
        {
            ItemsSource = new[] { "English", "Español" },
            SelectedIndex = _language == "es" ? 1 : 0,
            MinWidth = 280,
            Header = T("Display language", "Idioma de la interfaz")
        };
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(theme); panel.Children.Add(language);
        if (await ShowDialogAsync(NewDialog(T("Settings", "Configuración"), panel, T("Apply", "Aplicar"), T("Cancel", "Cancelar"))) == ContentDialogResult.Primary)
        {
            ApplyTheme(theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" });
            ApplyLanguage(language.SelectedIndex == 1 ? "es" : "en");
            await PersistAsync();
        }
    }
    private async Task HelpAsync()
    {
        (string Keys, string KeysEs, string English, string Spanish)[] shortcuts =
        [
            ("Ctrl+O", "Ctrl+O", "Open PDF", "Abrir PDF"),
            ("Ctrl+S", "Ctrl+S", "Save a copy", "Guardar una copia"),
            ("Ctrl+P", "Ctrl+P", "Print", "Imprimir"),
            ("Ctrl+W", "Ctrl+W", "Close document", "Cerrar documento"),
            ("Ctrl+F", "Ctrl+F", "Search", "Buscar"),
            ("Enter / Shift+Enter", "Enter / Mayús+Enter", "Next / previous match", "Coincidencia siguiente / anterior"),
            ("F3 / Shift+F3", "F3 / Mayús+F3", "Next / previous match", "Coincidencia siguiente / anterior"),
            ("Ctrl+G", "Ctrl+G", "Go to page", "Ir a la página"),
            ("Page Up / Page Down", "Re Pág / Av Pág", "Previous / next page", "Página anterior / siguiente"),
            ("Home / End", "Inicio / Fin", "First / last page (in document)", "Primera / última página"),
            ("Ctrl++ / Ctrl+-", "Ctrl++ / Ctrl+-", "Zoom in / out", "Acercar / alejar"),
            ("Ctrl+0 / Ctrl+1 / Ctrl+2", "Ctrl+0 / Ctrl+1 / Ctrl+2", "Fit page / actual size / fit width", "Ajustar página / tamaño real / ajustar al ancho"),
            ("Ctrl+R", "Ctrl+R", "Rotate clockwise", "Girar a la derecha"),
            ("Ctrl+C", "Ctrl+C", "Copy selected text", "Copiar texto seleccionado"),
            ("Ctrl+A", "Ctrl+A", "Select all text", "Seleccionar todo el texto"),
            ("F11", "F11", "Fullscreen", "Pantalla completa"),
            ("F5", "F5", "Presentation", "Presentación"),
            ("Escape", "Escape", "Exit presentation, fullscreen, or search", "Salir de presentación, pantalla completa o búsqueda")
        ];
        var notes = T(
            "Use Annotate to add sticky notes, highlights and drawings. They are saved in a separate .folio.json file beside the PDF. Right-click a highlight or drawing to remove it. PDFs stay on this device. Use Read scanned page to extract English or Spanish text locally, one page at a time. Select the result and press Ctrl+C to copy. OCR text is temporary and is not added to the PDF or document-wide search. Existing PDF annotations and form values are shown; filling in forms, editing PDF-native annotations and validating digital signatures are not supported.",
            "Usa Anotar para agregar notas adhesivas, resaltados y dibujos. Se guardan en un archivo .folio.json separado junto al PDF. Haz clic derecho en un resaltado o dibujo para quitarlo. Los PDF permanecen en este dispositivo. Usa Leer página escaneada para extraer texto en inglés o español de forma local, una página a la vez. Selecciona el resultado y presiona Ctrl+C para copiarlo. El texto OCR es temporal y no se agrega al PDF ni a la búsqueda del documento. Se muestran las anotaciones y los valores de formulario del PDF; no se admite rellenar formularios, editar anotaciones nativas del PDF ni validar firmas digitales.");
        // A two-column grid keeps the keys aligned; padded text cannot in a proportional font.
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (keys, keysEs, english, spanish) in shortcuts)
        {
            var row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = T(keys, keysEs), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var description = new TextBlock { Text = T(english, spanish), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(key, row); Grid.SetRow(description, row); Grid.SetColumn(description, 1);
            grid.Children.Add(key); grid.Children.Add(description);
        }
        var panel = new StackPanel { Spacing = 20, MaxWidth = 490, Padding = new Thickness(0, 0, 16, 0) };
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 });
        await ShowDialogAsync(NewDialog(T("Keyboard shortcuts", "Métodos abreviados de teclado"), new ScrollViewer { Content = panel, MaxHeight = 500 }, "", T("Done", "Listo")));
    }
    private async Task ActionAsync(string action)
    {
        switch (action)
        {
            case "open": await OpenPickerAsync(); break;
            case "save": await SaveCopyAsync(); break;
            case "print": await PrintAsync(); break;
            case "close": if (!_dialogOpen) CloseDocument(); break;
            case "find": ShowSearch(); break;
            case "annotate": if (_state.IsOpen && !_busy && !_printing) ShowPanel(AnnotationPanel); break;
            case "findNext": ShowSearch(); Find(true, false); break;
            case "findPrevious": ShowSearch(); Find(true, true); break;
            case "page": if (_state.IsOpen) { PageBox.Focus(FocusState.Programmatic); PageBox.SelectAll(); } break;
            case "next": Navigate(_state.Page + 1); break;
            case "previous": Navigate(_state.Page - 1); break;
            case "zoomIn": Zoom(1.2); break;
            case "zoomOut": Zoom(1 / 1.2); break;
            case "fitPage": Fit("page-fit"); break;
            case "fitWidth": Fit("page-width"); break;
            case "actualSize": Fit("1"); break;
            case "rotate": if (_state.IsOpen) DocumentView.Rotate(); break;
            case "fullscreen": if (_presentation) SetPresentation(false); else SetFullscreen(!_fullscreen); break;
            case "presentation": SetPresentation(!_presentation); break;
            case "escape":
                if (_dialogOpen) _activeDialog?.Hide();
                else if (_presentation) SetPresentation(false);
                else if (_fullscreen) SetFullscreen(false);
                else if (_busy) Cancel_Click(this, new RoutedEventArgs());
                else if (SearchPanel.Visibility == Visibility.Visible) CloseSearch();
                else HidePanel();
                break;
        }
    }
    private void AddShortcuts()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, string action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (sender, args) => { args.Handled = true; _ = RunAsync(() => ActionAsync(action)); };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        foreach (var (key, action) in new[] { (VirtualKey.O, "open"), (VirtualKey.S, "save"), (VirtualKey.P, "print"), (VirtualKey.W, "close"), (VirtualKey.F, "find"), (VirtualKey.G, "page"), (VirtualKey.R, "rotate"), (VirtualKey.Number0, "fitPage"), (VirtualKey.Number1, "actualSize"), (VirtualKey.Number2, "fitWidth") }) Add(key, VirtualKeyModifiers.Control, action);
        Add((VirtualKey)187, VirtualKeyModifiers.Control, "zoomIn"); Add((VirtualKey)189, VirtualKeyModifiers.Control, "zoomOut");
        Add((VirtualKey)187, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, "zoomIn");
        Add(VirtualKey.Add, VirtualKeyModifiers.Control, "zoomIn"); Add(VirtualKey.Subtract, VirtualKeyModifiers.Control, "zoomOut");
        Add(VirtualKey.F11, VirtualKeyModifiers.None, "fullscreen"); Add(VirtualKey.F5, VirtualKeyModifiers.None, "presentation");
        Add(VirtualKey.Escape, VirtualKeyModifiers.None, "escape");
        Add(VirtualKey.F3, VirtualKeyModifiers.None, "findNext"); Add(VirtualKey.F3, VirtualKeyModifiers.Shift, "findPrevious");
        Add(VirtualKey.PageDown, VirtualKeyModifiers.None, "next"); Add(VirtualKey.PageUp, VirtualKeyModifiers.None, "previous");
    }
    private void Thumbnails_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ThumbnailItem item || item.Image is not null || item.Requested) return;
        item.Requested = true; _thumbnailQueue.Add(item);
        // Requests the reader has already scrolled past are dropped and asked for again if they come back.
        if (_thumbnailQueue.Count > 40) { _thumbnailQueue[0].Requested = false; _thumbnailQueue.RemoveAt(0); }
        PumpThumbnails();
    }
    private void Thumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_syncing && Thumbnails.SelectedItem is ThumbnailItem item) { Navigate(item.Page); if (Root.ActualWidth < 900) DocumentSplit.IsPaneOpen = false; } }
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialized) return;
        UpdateSidebar(); var compact = e.NewSize.Width < 720;
        ZoomInButton.Visibility = ZoomOutButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PrivacyText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = !_busy && !_printing && e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.Caption = T("Open PDF in Folio", "Abrir PDF en Folio");
    }
    private async void Root_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            await RunAsync(async () =>
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count != 1 || items[0] is not StorageFile file) { ShowWarning(T("Drop one PDF file at a time.", "Arrastra un solo archivo PDF a la vez.")); return; }
                await OpenDocumentAsync(file.Path);
            });
        }
        finally { deferral.Complete(); }
    }
    private void Open_Click(object sender, RoutedEventArgs e) => _ = RunAsync(OpenPickerAsync);
    private void Save_Click(object sender, RoutedEventArgs e) => _ = RunAsync(SaveCopyAsync);
    private void Print_Click(object sender, RoutedEventArgs e) => _ = RunAsync(PrintAsync);
    private void Close_Click(object sender, RoutedEventArgs e) { if (!_dialogOpen) CloseDocument(); }
    private void TitleBar_BackRequested(TitleBar sender, object args) { if (!_dialogOpen && (_state.IsOpen || _busy)) CloseDocument(); }
    private void Previous_Click(object sender, RoutedEventArgs e) => Navigate(_state.Page - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(_state.Page + 1);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1 / 1.2);
    private void Scale_Click(object sender, RoutedEventArgs e) { if (sender is MenuFlyoutItem item) Fit(item.Tag.ToString()!); }
    private void FitWidth_Click(object sender, RoutedEventArgs e) => Fit("page-width");
    private void FitPage_Click(object sender, RoutedEventArgs e) => Fit("page-fit");
    private void Rotate_Click(object sender, RoutedEventArgs e) => DocumentView.Rotate();
    private void Find_Click(object sender, RoutedEventArgs e) => ShowSearch();
    private void Annotate_Click(object sender, RoutedEventArgs e) => _ = RunAsync(() => ActionAsync("annotate"));
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => Find(true, true);
    private void FindNext_Click(object sender, RoutedEventArgs e) => Find(true, false);
    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_initialized) return; _searchTimer.Stop(); _searchTimer.Start(); }
    private void MatchCase_Changed(object sender, RoutedEventArgs e) { if (_initialized) Find(false, false); }
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down); Find(true, shift); e.Handled = true; }
    }
    private void PageBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { CommitPage(); DocumentView.Focus(FocusState.Programmatic); e.Handled = true; } }
    private void PageBox_LostFocus(object sender, RoutedEventArgs e) => CommitPage();
    private void CommitPage() { if (int.TryParse(PageBox.Text, out var page)) Navigate(page); PageBox.Text = _state.Page.ToString(); }
    private void Sidebar_Click(object sender, RoutedEventArgs e)
    {
        DocumentSplit.IsPaneOpen = !DocumentSplit.IsPaneOpen;
        if (Root.ActualWidth >= 900) { _sidebarWanted = DocumentSplit.IsPaneOpen; _settings.Data.SidebarOpen = _sidebarWanted; _ = PersistAsync(); }
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => _ = RunAsync(() => ActionAsync("fullscreen"));
    private void Presentation_Click(object sender, RoutedEventArgs e) => SetPresentation(!_presentation);
    private void Settings_Click(object sender, RoutedEventArgs e) => _ = RunAsync(SettingsAsync);
    private void Help_Click(object sender, RoutedEventArgs e) => _ = RunAsync(HelpAsync);
    private void Ocr_Click(object sender, RoutedEventArgs e) { if (_state.IsOpen && !_busy && !_printing) ShowPanel(OcrPanel); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_printing) { _printCancel?.Cancel(); _printing = false; EndBusy(); } else CloseDocument(); }
}
