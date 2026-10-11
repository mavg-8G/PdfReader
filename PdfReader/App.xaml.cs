using Microsoft.UI.Xaml;
namespace PdfReader;

public partial class App : Application
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folio");
    private Window? _window;
    public App()
    {
        Directory.CreateDirectory(DataFolder);
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(); _window.Activate();
    }
}
