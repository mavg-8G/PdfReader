using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace PdfReader.ViewModels;

public sealed class ThumbnailItem(int page) : INotifyPropertyChanged
{
    private BitmapImage? _image;
    public int Page { get; } = page;
    public string Label => $"Page {Page}";
    public BitmapImage? Image { get => _image; set { _image = value; OnPropertyChanged(); } }
    public bool Requested { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
