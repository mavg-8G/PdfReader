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
        WelcomeContent.Width = Math.Max(240, Math.Min(1120, e.NewSize.Width - 48));
        var stacked = e.NewSize.Width < 820;
        var compact = e.NewSize.Width < 520;
        WelcomeContent.Margin = new Thickness(compact ? 18 : 28);
        DashboardGrid.ColumnSpacing = stacked ? 0 : 20;
        DashboardGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        DashboardGrid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : .8, GridUnitType.Star);
        DashboardGrid.RowDefinitions[1].Height = new GridLength(stacked ? 1 : 0, stacked ? GridUnitType.Auto : GridUnitType.Pixel);
        Grid.SetColumn(RecentPanel, stacked ? 0 : 1);
        Grid.SetRow(RecentPanel, stacked ? 1 : 0);
        HeroCard.MinHeight = compact ? 450 : 520;
        RecentPanel.MinHeight = stacked ? 360 : 520;
        DocumentArtwork.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        WelcomeHeading.FontSize = compact ? 31 : 38;
    }
    public void SetRecent(IReadOnlyList<RecentDocument> recent)
    {
        RecentList.ItemsSource = recent.ToArray();
        EmptyRecents.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public void ApplyLanguage(string language)
    {
        var spanish = language == "es";
        EyebrowText.Text = spanish ? "LECTOR PDF PARA WINDOWS" : "PDF READER FOR WINDOWS";
        WelcomeHeading.Text = spanish ? "Tus documentos, sin distracciones" : "Your documents, without the clutter";
        WelcomeSubtitle.Text = spanish ? "Un espacio privado y enfocado para leer PDF en Windows." : "A focused, private place to read PDFs on Windows.";
        OpenHeading.Text = spanish ? "Empieza a leer" : "Start reading";
        OpenDescription.Text = spanish ? "Elige un PDF de tu dispositivo. También puedes arrastrar un archivo a esta ventana." : "Choose a PDF from your device. You can also drag and drop a file into this window.";
        ChooseDocumentText.Text = spanish ? "Abrir PDF" : "Open PDF";
        RecentHeading.Text = spanish ? "Archivos recientes" : "Recent files";
        RecentDescription.Text = spanish ? "Continúa donde lo dejaste." : "Continue where you left off.";
        ClearButton.Content = spanish ? "Borrar" : "Clear";
        EmptyHeading.Text = spanish ? "Aún no hay archivos" : "Nothing here yet";
        EmptyDescription.Text = spanish ? "Los documentos que abras aparecerán aquí y recordarán la última página." : "Documents you open will appear here with their last page remembered.";
        PrivacyMessage.Text = spanish ? "Privado por diseño" : "Private by design";
        DropHintText.Text = spanish ? "Los archivos permanecen en este dispositivo y puedes soltarlos en cualquier parte de Folio." : "Files stay on this device and can be dropped anywhere in Folio.";
        AutomationProperties.SetName(OpenDocumentButton, spanish ? "Abrir un PDF desde el dispositivo" : "Open a PDF from your device");
        AutomationProperties.SetName(RecentList, spanish ? "Archivos PDF abiertos recientemente" : "Recently opened PDFs");
    }
    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    private void Clear_Click(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();
    private void Recent_Click(object sender, ItemClickEventArgs e) { if (e.ClickedItem is RecentDocument recent) RecentRequested?.Invoke(recent.Path); }
}
