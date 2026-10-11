using Microsoft.UI.Dispatching;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PdfReader.Core;
using PdfReader.Services;
using PdfReader.ViewModels;

namespace PdfReader;

public sealed partial class MainWindow : Window
{
    private readonly ReaderState _state = new();
    private readonly SettingsStore _settings;
    private readonly PrintService _print;
    private readonly DispatcherQueueTimer _saveTimer, _searchTimer;
    private readonly List<ThumbnailItem> _thumbnails = [];
    private readonly List<ThumbnailItem> _thumbnailQueue = [];
    private DocumentSession? _document;
    private PdfDocument? _pdf;
    private DocumentMarkupStore? _markupStore;
    private readonly LinkedList<int> _thumbnailCache = [];
    private bool _initialized, _busy, _printing, _syncing, _closing, _closed, _presentation, _fullscreen, _canPrint, _thumbnailPump;
    private bool _dialogOpen, _sidebarWanted = true;
    private string _language = "en";
    private ContentDialog? _activeDialog;
    private CancellationTokenSource? _opening, _printCancel, _ocrCancel;
    public MainWindow()
    {
        _settings = new SettingsStore(Path.Combine(App.DataFolder, "settings.json"));
        // Read the tiny local settings file before the window is activated. This keeps
        // the first frame in the saved theme instead of briefly showing the system theme.
        _settings.Load();
        _language = _settings.Data.Language;
        _sidebarWanted = _settings.Data.SidebarOpen;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Folio.ico"));
        _print = new PrintService(this);
        DocumentView.StateChanged += OnViewerState;
        DocumentView.SearchChanged += OnSearchStatus;
        DocumentView.Warning += message => ShowWarning(TranslateKnown(message));
        DocumentView.MarkupChanged += () => _ = RunAsync(SaveMarkupAsync);
        DocumentView.ToolChanged += UpdateAnnotationPanel;
        DocumentView.ExitPresentationRequested += () => SetPresentation(false);
        ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 880)); ApplyMinimumSize();
        ApplyTheme(_settings.Data.Theme);
        ApplyLanguage(_language);
        Welcome.SetRecent(_settings.Data.Recent);
        _saveTimer = DispatcherQueue.CreateTimer(); _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _ = PersistAsync(); };
        _searchTimer = DispatcherQueue.CreateTimer(); _searchTimer.Interval = TimeSpan.FromMilliseconds(200);
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Find(false, false); };
        Welcome.OpenRequested += () => _ = RunAsync(OpenPickerAsync);
        Welcome.RecentRequested += path => _ = RunAsync(() => OpenDocumentAsync(path));
        Welcome.ClearRequested += () => { _settings.Data.Recent.Clear(); Welcome.SetRecent(_settings.Data.Recent); _ = PersistAsync(); };
        Welcome.RemoveRequested += path => { _settings.Data.Recent.RemoveAll(r => r.Path == path); Welcome.SetRecent(_settings.Data.Recent); _ = PersistAsync(); };
        AddShortcuts();
        AppWindow.Closing += async (_, args) =>
        {
            if (_closing) return;
            args.Cancel = true; _closing = true; _activeDialog?.Hide();
            // Pending annotation edits are written before the window goes away.
            DocumentView.FlushMarkup();
            await PersistAsync(); Close();
        };
        Closed += (_, _) =>
        {
            _closed = true; _saveTimer.Stop(); _searchTimer.Stop();
            _opening?.Cancel(); _printCancel?.Cancel(); _ocrCancel?.Cancel();
            DocumentView.Close(); _pdf?.Dispose(); _document?.Dispose(); _pdf = null; _document = null;
        };
    }
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return; _initialized = true;
        await RunAsync(async () =>
        {
            Welcome.SetRecent(_settings.Data.Recent);
            if (_settings.LastError is { } warning) ShowWarning(TranslateKnown(warning));
            var path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(p => !p.StartsWith("--", StringComparison.Ordinal));
            if (path is not null) await OpenDocumentAsync(path);
        });
    }
    private async Task RunAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_closed) return;
            var (title, message) = ex switch
            {
                DocumentException de => (TranslateKnown(de.Title), TranslateKnown(de.Message)),
                FileNotFoundException => (T("This file has moved", "Este archivo se movió"), T("Choose the PDF again from its current location.", "Vuelve a elegir el PDF desde su ubicación actual.")),
                DirectoryNotFoundException => (T("This folder is unavailable", "Esta carpeta no está disponible"), T("Reconnect the device or choose the PDF from another folder.", "Vuelve a conectar el dispositivo o elige el PDF desde otra carpeta.")),
                UnauthorizedAccessException => (T("Access to this file was denied", "Se denegó el acceso a este archivo"), T("Choose a file or folder you have permission to use.", "Elige un archivo o una carpeta para los que tengas permiso.")),
                IOException => (T("The file could not be accessed", "No se pudo acceder al archivo"), T("It may be in use, unavailable, or the device may be full. Check the file and try again.", "Puede estar en uso, no disponible o el dispositivo puede estar lleno. Comprueba el archivo e inténtalo de nuevo.")),
                _ => (T("The action could not be completed", "No se pudo completar la acción"), TranslateKnown(ex.Message))
            };
            ShowError(title, message); EndBusy();
        }
    }
}
