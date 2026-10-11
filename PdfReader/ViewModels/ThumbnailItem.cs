using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace PdfReader.ViewModels;

public sealed class ThumbnailItem(int page) : INotifyPropertyChanged
{
    private ImageSource? _image;
    public int Page { get; } = page;
    public string Label => $"Page {Page}";
    public ImageSource? Image { get => _image; set { _image = value; OnPropertyChanged(); } }
    public bool Requested { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
