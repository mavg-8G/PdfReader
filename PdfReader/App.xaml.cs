using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Reflection;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
namespace PdfReader;

public partial class App : Application
{
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folio");
    /// <summary>Notes, highlights and drawings for every PDF live here, one file per document.</summary>
    public static string AnnotationsFolder { get; } = Path.Combine(DataFolder, "Annotations");
    // Null when Folio runs unpackaged: from Visual Studio, a published folder or the MSI.
    private static readonly Package? Installed = FindPackage();
    /// <summary>
    /// The version in Instalador/Package.appxmanifest: read from the installed package when there is one,
    /// otherwise from the copy the build stamps into the assembly.
    /// </summary>
    public static string Version { get; } = ReadVersion();
    /// <summary>The Windows Settings page where the user can make Folio the default app for .pdf.</summary>
    public static Uri DefaultAppsUri { get; } = new(Installed is { } package
        ? $"ms-settings:defaultapps?registeredAUMID={package.Id.FamilyName}!App"
        : "ms-settings:defaultapps");
    /// <summary>The PDF Windows asked Folio to open through its file association, if that is how it was started.</summary>
    public static string? ActivatedFile { get; private set; }
    private Window? _window;
    public App()
    {
        Directory.CreateDirectory(DataFolder);
        InitializeComponent();
    }
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // The activation arguments must be read here. Asking for them later, from inside a XAML event such as
        // Loaded, makes the packaged app fail fast inside the call.
        ActivatedFile = ReadActivatedFile();
        _window = new MainWindow(); _window.Activate();
    }
    private static Package? FindPackage()
    {
        // Package.Current throws when the process has no package identity.
        try { return Package.Current; } catch (InvalidOperationException) { return null; }
    }
    private static string ReadVersion()
    {
        if (Installed is { } package) { var version = package.Id.Version; return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"; }
        var assembly = typeof(App).Assembly;
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == "PackageVersion")?.Value
            ?? assembly.GetName().Version?.ToString() ?? "";
    }
    private static string? ReadActivatedFile()
    {
        try
        {
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            return activation.Kind == ExtendedActivationKind.File && activation.Data is IFileActivatedEventArgs file && file.Files.Count > 0 ? file.Files[0].Path : null;
        }
        // Not knowing how Folio was started only means it opens on the welcome screen.
        catch (Exception) { return null; }
    }
}
