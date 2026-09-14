using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PdfReader.Core;
namespace PdfReader.Views;

public sealed partial class WelcomeView : UserControl
{
    public event Action? OpenRequested;
    public event Action? ClearRequested;
    public event Action<string>? RecentRequested;
    public WelcomeView() => InitializeComponent();
    private void Scroll_SizeChanged(object sender, SizeChangedEventArgs e) => WelcomeContent.Width = Math.Max(240, Math.Min(840, e.NewSize.Width - 64));
    public void SetRecent(IReadOnlyList<RecentDocument> recent)
    {
        RecentList.ItemsSource = recent.ToArray();
        EmptyRecents.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    private void Clear_Click(object sender, RoutedEventArgs e) => ClearRequested?.Invoke();
    private void Recent_Click(object sender, ItemClickEventArgs e) { if (e.ClickedItem is RecentDocument recent) RecentRequested?.Invoke(recent.Path); }
}
