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
    private readonly ViewerHost _host;
    private readonly DispatcherQueueTimer _saveTimer, _searchTimer, _openTimer;
    private readonly TaskCompletionSource _viewerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ThumbnailItem> _thumbnails = [];
    private readonly LinkedList<int> _thumbnailCache = [];
    private bool _initialized, _busy, _printing, _syncing, _closing, _closed, _presentation, _fullscreen, _canPrint;
    private bool _dialogOpen, _sidebarWanted = true;
    private string _language = "en";
    private ContentDialog? _activeDialog;
    private string? _pendingPath;
    private CancellationTokenSource? _opening;
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
        _host = new ViewerHost(DocumentView);
        _host.Message += message => _ = RunAsync(() => ReceiveAsync(message));
        _host.Failed += message => { _viewerReady.TrySetException(new InvalidOperationException(message)); ShowError(T("Viewer unavailable", "Visor no disponible"), TranslateKnown(message)); EndBusy(); };
        _host.FileDropped += path => _ = RunAsync(() => OpenDocumentAsync(path));
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleDragArea);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 880));
        ApplyTheme(_settings.Data.Theme);
        ApplyLanguage(_language);
        Welcome.SetRecent(_settings.Data.Recent);
        _saveTimer = DispatcherQueue.CreateTimer(); _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _ = PersistAsync(); };
        _searchTimer = DispatcherQueue.CreateTimer(); _searchTimer.Interval = TimeSpan.FromMilliseconds(200);
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Find(false, false); };
        _openTimer = DispatcherQueue.CreateTimer(); _openTimer.Interval = TimeSpan.FromSeconds(60);
        _openTimer.Tick += (_, _) => { _openTimer.Stop(); ShowError(T("This PDF is taking too long", "Este PDF está tardando demasiado"), T("Opening was stopped. Try a smaller file or obtain a new copy of this PDF.", "Se detuvo la apertura. Prueba con un archivo más pequeño u obtén una copia nueva del PDF.")); CloseDocument(); };
        Welcome.OpenRequested += () => _ = RunAsync(OpenPickerAsync);
        Welcome.RecentRequested += path => _ = RunAsync(() => OpenDocumentAsync(path));
        Welcome.ClearRequested += () => { _settings.Data.Recent.Clear(); Welcome.SetRecent(_settings.Data.Recent); _ = PersistAsync(); };
        Root.ActualThemeChanged += (_, _) => SendTheme();
        AddShortcuts();
        AppWindow.Closing += async (_, args) =>
        {
            if (_closing) return;
            args.Cancel = true; _closing = true; _activeDialog?.Hide();
            await PersistAsync(); Close();
        };
        Closed += (_, _) =>
        {
            _closed = true; _saveTimer.Stop(); _searchTimer.Stop(); _openTimer.Stop();
            _opening?.Cancel(); _host.Dispose();
        };
    }
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return; _initialized = true;
        await RunAsync(async () =>
        {
            Welcome.SetRecent(_settings.Data.Recent);
            if (_settings.LastError is { } warning) ShowWarning(TranslateKnown(warning));
            await _host.InitializeAsync();
            await _viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(30)); SendTheme(); SendLanguage();
            var path = _pendingPath ?? Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(p => !p.StartsWith("--", StringComparison.Ordinal));
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
                TimeoutException => (T("The viewer did not respond", "El visor no respondió"), T("Restart Folio. If the issue continues, repair the Microsoft Edge WebView2 Runtime.", "Reinicia Folio. Si el problema continúa, repara Microsoft Edge WebView2 Runtime.")),
                _ => (T("The action could not be completed", "No se pudo completar la acción"), TranslateKnown(ex.Message))
            };
            ShowError(title, message); EndBusy();
        }
    }
}
