using Microsoft.UI.Dispatching;
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
    private ContentDialog? _activeDialog;
    private string? _pendingPath;
    private CancellationTokenSource? _opening;
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Folio.ico"));
        _settings = new SettingsStore(Path.Combine(App.DataFolder, "settings.json"));
        _host = new ViewerHost(DocumentView);
        _host.Message += message => _ = RunAsync(() => ReceiveAsync(message));
        _host.Failed += message => { _viewerReady.TrySetException(new InvalidOperationException(message)); ShowError("Viewer unavailable", message); EndBusy(); };
        _host.FileDropped += path => _ = RunAsync(() => OpenDocumentAsync(path));
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleDragArea);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 880));
        _saveTimer = DispatcherQueue.CreateTimer(); _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _ = PersistAsync(); };
        _searchTimer = DispatcherQueue.CreateTimer(); _searchTimer.Interval = TimeSpan.FromMilliseconds(200);
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Find(false, false); };
        _openTimer = DispatcherQueue.CreateTimer(); _openTimer.Interval = TimeSpan.FromSeconds(60);
        _openTimer.Tick += (_, _) => { _openTimer.Stop(); ShowError("This PDF is taking too long", "Opening was stopped. Try a smaller file or obtain a new copy of this PDF."); CloseDocument(); };
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
            await _settings.LoadAsync(); _sidebarWanted = _settings.Data.SidebarOpen;
            ApplyTheme(_settings.Data.Theme); Welcome.SetRecent(_settings.Data.Recent);
            if (_settings.LastError is { } warning) ShowWarning(warning);
            await _host.InitializeAsync();
            await _viewerReady.Task.WaitAsync(TimeSpan.FromSeconds(30)); SendTheme();
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
                DocumentException de => (de.Title, de.Message),
                FileNotFoundException => ("This file has moved", "Choose the PDF again from its current location."),
                DirectoryNotFoundException => ("This folder is unavailable", "Reconnect the device or choose the PDF from another folder."),
                UnauthorizedAccessException => ("Access to this file was denied", "Choose a file or folder you have permission to use."),
                IOException => ("The file could not be accessed", "It may be in use, unavailable, or the device may be full. Check the file and try again."),
                TimeoutException => ("The viewer did not respond", "Restart Folio. If the issue continues, repair the Microsoft Edge WebView2 Runtime."),
                _ => ("The action could not be completed", ex.Message)
            };
            ShowError(title, message); EndBusy();
        }
    }
}
