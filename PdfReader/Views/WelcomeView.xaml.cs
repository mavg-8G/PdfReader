using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PdfReader.Core;
namespace PdfReader.Views;

public sealed partial class WelcomeView : UserControl
{
    public event Action? OpenRequested;
    public event Action? ClearRequested;
    public event Action<string>? RecentRequested;
    public WelcomeView() => InitializeComponent();
    private void Scroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WelcomeContent.Width = Math.Max(240, Math.Min(900, e.NewSize.Width - 48));
        var narrow = e.NewSize.Width < 560;
        WelcomeContent.Margin = new Thickness(narrow ? 24 : 36, narrow ? 32 : 48, narrow ? 24 : 36, 36);
        DocumentArtwork.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(OpenCardText, narrow ? 0 : 1);
        Grid.SetColumn(OpenActions, narrow ? 0 : 1);
    }
    public void SetRecent(IReadOnlyList<RecentDocument> recent)
    {
        RecentList.ItemsSource = recent.ToArray();
        EmptyRecents.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentCard.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public void ApplyLanguage(string language)
    {
        var spanish = language == "es";
        EyebrowText.Text = spanish ? "LECTOR PDF PARA WINDOWS" : "PDF READER FOR WINDOWS";
        WelcomeHeading.Text = spanish ? "Te damos la bienvenida a Folio" : "Welcome to Folio";
        WelcomeSubtitle.Text = spanish ? "Abre un documento y continúa justo donde lo dejaste." : "Open a document and keep reading right where you left off.";
        OpenHeading.Text = spanish ? "Abrir un PDF" : "Open a PDF";
        OpenDescription.Text = spanish ? "Elige un documento de tu dispositivo o arrástralo a cualquier parte de esta ventana." : "Choose a document from your device, or drag one anywhere into this window.";
        ChooseDocumentText.Text = spanish ? "Elegir PDF" : "Choose PDF";
        RecentHeading.Text = spanish ? "Documentos recientes" : "Recent documents";
        RecentDescription.Text = spanish ? "Continúa desde la última página." : "Continue from your last page.";
        ClearButton.Content = spanish ? "Borrar historial" : "Clear history";
        EmptyHeading.Text = spanish ? "No hay documentos recientes" : "No recent documents";
        EmptyDescription.Text = spanish ? "Los PDF que abras aparecerán aquí y recordarán la última página." : "PDFs you open will appear here with their last page remembered.";
        PrivacyMessage.Text = spanish ? "Privado por diseño. Tus PDF permanecen en este dispositivo." : "Private by design. Your PDFs stay on this device.";
        AutomationProperties.SetName(OpenDocumentButton, spanish ? "Abrir un PDF desde el dispositivo" : "Open a PDF from your device");
        AutomationProperties.SetName(RecentList, spanish ? "Archivos PDF abiertos recientemente" : "Recently opened PDFs");
    }
    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    private void Clear_Click(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();
    private void Recent_Click(object sender, ItemClickEventArgs e) { if (e.ClickedItem is RecentDocument recent) RecentRequested?.Invoke(recent.Path); }
}
