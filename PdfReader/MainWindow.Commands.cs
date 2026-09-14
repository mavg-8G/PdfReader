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
    private void Navigate(int page) { if (_state.IsOpen && !_busy) Send("page", value: _state.Navigate(page)); }
    private void Zoom(double factor) { if (_state.IsOpen && !_busy) Send("zoom", value: _state.SetZoom(_state.Zoom * factor)); }
    private void Fit(string scale) { if (_state.IsOpen) Send("zoom", value: scale); }
    private void Send(string type, object? value = null) => _host.Send(new { type, id = _host.Document?.Id, value });
    private void SetBusy(string message) { _busy = true; BusyText.Text = message; BusyOverlay.Visibility = Visibility.Visible; LoadingBar.Visibility = Visibility.Visible; LoadingBar.IsIndeterminate = true; }
    private void EndBusy() { _busy = false; BusyOverlay.Visibility = Visibility.Collapsed; LoadingBar.Visibility = Visibility.Collapsed; }
    private void Remember()
    {
        if (_host.Document is { } document && _state.IsOpen) { _settings.Remember(document, _state); _saveTimer.Stop(); _saveTimer.Start(); }
    }
    private async Task PersistAsync()
    {
        try
        {
            if (_host.Document is { } document && _state.IsOpen) _settings.Remember(document, _state);
            if (!await _settings.SaveAsync() && !_closed) ShowWarning(_settings.LastError!);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private void UpdateControls()
    {
        NavigationControls.IsEnabled = FindButton.IsEnabled = OcrButton.IsEnabled = RotateButton.IsEnabled = SaveButton.IsEnabled = _state.IsOpen;
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
        _host.Send(new { type = "find", id = _host.Document!.Id, query = SearchBox.Text, again, previous, caseSensitive = MatchCaseBox.IsChecked == true });
    }
    private void CloseSearch() { SearchPanel.Visibility = Visibility.Collapsed; _searchTimer.Stop(); Send("closeFind"); Send("focus"); }
    private void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        _settings.Data.Theme = theme; SendTheme();
    }
    private void SendTheme() => _host.Send(new { type = "theme", value = Root.ActualTheme == ElementTheme.Dark ? "dark" : "light" });
    private void SendLanguage() => _host.Send(new { type = "language", value = _language });
    private void UpdateSidebar()
    {
        DocumentSplit.DisplayMode = Root.ActualWidth < 900 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        DocumentSplit.IsPaneOpen = _state.IsOpen && _sidebarWanted && Root.ActualWidth >= 900 && !_presentation;
    }
    private void SetFullscreen(bool enabled)
    {
        _fullscreen = enabled; AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
    }
    private void SetPresentation(bool enabled)
    {
        if (enabled && (!_state.IsOpen || _busy)) return;
        _presentation = enabled; Send("presentation", value: enabled); SetFullscreen(enabled);
        ToolbarArea.Visibility = TitleDragArea.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        Root.RowDefinitions[0].Height = new GridLength(enabled ? 0 : 48);
        Notices.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        Root.RowDefinitions[4].Height = new GridLength(enabled ? 0 : 32); UpdateSidebar();
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
        var panel = new StackPanel { Spacing = 18 };
        panel.Children.Add(new TextBlock
        {
            Text = T("Personalize Folio. Changes apply immediately.", "Personaliza Folio. Los cambios se aplican de inmediato."),
            Opacity = .7,
            TextWrapping = TextWrapping.Wrap
        });
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
        var text = T(
            "Ctrl+O     Open PDF\nCtrl+S     Save a copy\nCtrl+P     Print\nCtrl+W     Close document\nCtrl+F     Search\nEnter / Shift+Enter     Next / previous match\nF3 / Shift+F3     Next / previous match\nCtrl+G     Go to page\nPage Up / Page Down     Previous / next page\nHome / End     First / last page (in document)\nCtrl++ / Ctrl+-     Zoom in / out\nCtrl+0 / Ctrl+1 / Ctrl+2     Fit page / actual size / fit width\nCtrl+R     Rotate clockwise\nCtrl+C     Copy selected text\nF11     Fullscreen\nF5     Presentation\nEscape     Exit presentation, fullscreen, or search\n\nPDFs stay on this device. Use Read scanned page to extract English or Spanish text locally, one page at a time. Select the result and press Ctrl+C to copy. OCR text is temporary and is not added to the PDF or document-wide search. Existing annotations are shown; editing and digital signature validation are not supported.",
            "Ctrl+O     Abrir PDF\nCtrl+S     Guardar una copia\nCtrl+P     Imprimir\nCtrl+W     Cerrar documento\nCtrl+F     Buscar\nEnter / Mayús+Enter     Coincidencia siguiente / anterior\nF3 / Mayús+F3     Coincidencia siguiente / anterior\nCtrl+G     Ir a la página\nRe Pág / Av Pág     Página anterior / siguiente\nInicio / Fin     Primera / última página\nCtrl++ / Ctrl+-     Acercar / alejar\nCtrl+0 / Ctrl+1 / Ctrl+2     Ajustar página / tamaño real / ajustar al ancho\nCtrl+R     Girar a la derecha\nCtrl+C     Copiar texto seleccionado\nF11     Pantalla completa\nF5     Presentación\nEscape     Salir de presentación, pantalla completa o búsqueda\n\nLos PDF permanecen en este dispositivo. Usa Leer página escaneada para extraer texto en inglés o español de forma local, una página a la vez. Selecciona el resultado y presiona Ctrl+C para copiarlo. El texto OCR es temporal y no se agrega al PDF ni a la búsqueda del documento. Se muestran las anotaciones existentes; no se admite la edición ni la validación de firmas digitales.");
        await ShowDialogAsync(NewDialog(T("Reading with Folio", "Leer con Folio"), new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 490 }, MaxHeight = 500 }, "", T("Done", "Listo")));
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
            case "rotate": if (_state.IsOpen) Send("rotate"); break;
            case "fullscreen": if (_presentation) SetPresentation(false); else SetFullscreen(!_fullscreen); break;
            case "presentation": SetPresentation(!_presentation); break;
            case "escape":
                if (_dialogOpen) _activeDialog?.Hide();
                else if (_presentation) SetPresentation(false);
                else if (_fullscreen) SetFullscreen(false);
                else if (_busy) Cancel_Click(this, new RoutedEventArgs());
                else { Send("closeOcr"); CloseSearch(); }
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
        item.Requested = true; _host.Send(new { type = "thumbnail", id = _host.Document?.Id, page = item.Page });
    }
    private void Thumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_syncing && Thumbnails.SelectedItem is ThumbnailItem item) { Navigate(item.Page); if (Root.ActualWidth < 900) DocumentSplit.IsPaneOpen = false; } }
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialized) return;
        UpdateSidebar(); var compact = e.NewSize.Width < 600;
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
    private void Previous_Click(object sender, RoutedEventArgs e) => Navigate(_state.Page - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(_state.Page + 1);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1 / 1.2);
    private void Scale_Click(object sender, RoutedEventArgs e) { if (sender is MenuFlyoutItem item) Fit(item.Tag.ToString()!); }
    private void FitWidth_Click(object sender, RoutedEventArgs e) => Fit("page-width");
    private void FitPage_Click(object sender, RoutedEventArgs e) => Fit("page-fit");
    private void Rotate_Click(object sender, RoutedEventArgs e) => Send("rotate");
    private void Find_Click(object sender, RoutedEventArgs e) => ShowSearch();
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => Find(true, true);
    private void FindNext_Click(object sender, RoutedEventArgs e) => Find(true, false);
    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_initialized) return; _searchTimer.Stop(); _searchTimer.Start(); }
    private void MatchCase_Changed(object sender, RoutedEventArgs e) { if (_initialized) Find(false, false); }
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down); Find(true, shift); e.Handled = true; }
    }
    private void PageBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { CommitPage(); Send("focus"); e.Handled = true; } }
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
    private void Ocr_Click(object sender, RoutedEventArgs e) { if (_state.IsOpen && !_busy && !_printing) { Send("ocr"); DocumentView.Focus(FocusState.Programmatic); } }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_printing) { Send("cancelPrint"); _printing = false; EndBusy(); } else CloseDocument(); }
}
