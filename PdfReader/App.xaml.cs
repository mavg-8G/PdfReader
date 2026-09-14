using Microsoft.UI.Xaml;
namespace PdfReader;
public partial class App : Application
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folio");
    private Window? _window;
    public App()
    {
        Directory.CreateDirectory(DataFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(DataFolder, "WebView2"));
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(); _window.Activate();
    }
}
