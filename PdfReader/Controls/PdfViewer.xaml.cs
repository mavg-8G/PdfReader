using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfReader.Core;
using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PdfReader.Controls;

public enum AnnotationTool { None, Note, Draw }
public readonly record struct ViewerState(int Page, double Zoom, string Scale, int Rotation);
public readonly record struct SearchStatus(int Current, int Total, bool Pending);

/// <summary>
/// Shows a PDF as a scrolling column of pages rendered by PDFium. Only the pages near the viewport
/// exist as elements; everything else is arithmetic over the page tops.
/// </summary>
public sealed partial class PdfViewer : UserControl
{
    private const double DipsPerPoint = 96.0 / 72, Gap = 16, Edge = 16;
    private const long MaximumPagePixels = 12_000_000;

    private sealed class PageElement(int page)
    {
        public int Page { get; } = page;
        public Grid Root { get; } = new();
        public Image Image { get; } = new() { Stretch = Stretch.Fill };
        public Canvas Marks { get; } = new() { IsHitTestVisible = false };
        public Canvas Notes { get; } = new();
        public int RenderedWidth, RenderedHeight, RenderedRotation = -1;
        public bool Failed;
        public PdfPageText? Text;
    }

    private PdfDocument? _pdf;
    private int _generation, _page = 1, _rotation;
    private double _zoom = 1;
    private string _scale = "page-width";
    // Top edge of every page in DIPs, plus one final entry for the bottom of the document.
    private double[] _tops = [];
    private (int Page, double Fraction)? _pendingAnchor;
    private bool _presentation, _pumping, _warned;
    private string _language = "en";
    private readonly Dictionary<int, PageElement> _realized = [];
    private readonly Dictionary<int, Task<PdfPageText?>> _text = [];
    private readonly Queue<int> _textOrder = new();

    public event Action<ViewerState>? StateChanged;
    public event Action<SearchStatus>? SearchChanged;
    public event Action<string>? Warning;
    public event Action? MarkupChanged;
    public event Action? ToolChanged;
    public event Action? HistoryChanged;
    public event Action? ExitPresentationRequested;

    public PdfViewer()
    {
        InitializeComponent();
        _markupTimer = DispatcherQueue.CreateTimer(); _markupTimer.Interval = TimeSpan.FromMilliseconds(450);
        _markupTimer.Tick += (_, _) => { _markupTimer.Stop(); MarkupChanged?.Invoke(); };
        ActualThemeChanged += (_, _) => RedrawAll();
        UiLanguage = "en";
    }

    public bool IsOpen => _pdf is not null;
    public int CurrentPage => _page;
    private string T(string english, string spanish) => _language == "es" ? spanish : english;

    public string UiLanguage
    {
        get => _language;
        set
        {
            _language = value == "es" ? "es" : "en";
            ExitPresentationButton.Content = T("Exit presentation · Esc", "Salir de la presentación · Esc");
            RedrawAll();
        }
    }

    public void Open(PdfDocument pdf, DocumentMarkup markup, int page, string scale, int rotation)
    {
        Close();
        _pdf = pdf; _markup = markup;
        _rotation = rotation is 90 or 180 or 270 ? rotation : 0;
        _scale = NormalizeScale(scale); _page = Math.Clamp(page, 1, pdf.PageCount);
        Relayout(_page, 0);
        RaiseState();
    }

    public void Close()
    {
        FlushMarkup();
        ++_generation; _pdf = null; _warned = false;
        _searchCancel?.Cancel(); _searchCancel = null; _matches.Clear(); _matchesByPage.Clear(); _currentMatch = -1; _query = ""; _searchPending = false;
        _anchor = _focus = null; _dragging = false; _stroke = null; _strokeLine = null;
        _recognized.Clear();
        if (_undo.Count + _redo.Count > 0) { _undo.Clear(); _redo.Clear(); HistoryChanged?.Invoke(); }
        _markup = DocumentMarkup.Empty(""); SetTool(AnnotationTool.None);
        _realized.Clear(); _text.Clear(); _textOrder.Clear(); Pages.Children.Clear();
        _tops = []; _pendingAnchor = null; _page = 1; _rotation = 0; _zoom = 1; _scale = "page-width";
        if (_presentation) { _presentation = false; ApplyPresentationChrome(); }
        Pages.Width = Pages.Height = 0;
    }

    // ---- Layout -------------------------------------------------------------------------

    private static string NormalizeScale(string? scale)
    {
        if (scale is "page-width" or "page-fit" or "auto") return scale;
        return double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? Math.Clamp(value, .25, 5).ToString(CultureInfo.InvariantCulture) : "page-width";
    }

    private (double Width, double Height) PageDips(int page, double zoom)
    {
        var size = _pdf!.GetPageSize(page);
        return _rotation is 90 or 270 ? (size.Height * DipsPerPoint * zoom, size.Width * DipsPerPoint * zoom) : (size.Width * DipsPerPoint * zoom, size.Height * DipsPerPoint * zoom);
    }

    private double ResolveZoom(int page, double viewportWidth, double viewportHeight)
    {
        var (width, height) = PageDips(page, 1);
        // Leave room for the vertical scrollbar so a width fit never produces a horizontal one.
        var fitWidth = (viewportWidth - Edge * 2 - 16) / width;
        var zoom = _scale switch
        {
            "page-fit" => Math.Min(fitWidth, (viewportHeight - Edge * 2) / height),
            "page-width" => fitWidth,
            "auto" => Math.Min(fitWidth, 1.25),
            _ => double.Parse(_scale, CultureInfo.InvariantCulture)
        };
        return Math.Clamp(zoom, .1, 10);
    }

    private (int Page, double Fraction) CaptureAnchor()
    {
        if (_pdf is null || _tops.Length == 0 || _presentation) return (_page, 0);
        var offset = Scroller.VerticalOffset;
        var page = PageAt(offset + Edge);
        var height = _tops[page] - _tops[page - 1] - Gap;
        return (page, height > 0 ? (offset - _tops[page - 1]) / height : 0);
    }

    /// <summary>Recomputes every page position and keeps <paramref name="anchorPage"/> where it was in the viewport.</summary>
    private void Relayout(int anchorPage, double fraction)
    {
        if (_pdf is not { } pdf) return;
        double viewportWidth = Scroller.ActualWidth, viewportHeight = Scroller.ActualHeight;
        // Before the first layout pass there is no viewport to fit to; Scroller_SizeChanged finishes the job.
        if (viewportWidth <= 0 || viewportHeight <= 0) { _pendingAnchor = (anchorPage, fraction); return; }
        _pendingAnchor = null;
        if (_presentation) { LayoutPresentation(viewportWidth, viewportHeight); return; }

        var horizontal = Pages.Width > viewportWidth ? (Scroller.HorizontalOffset + viewportWidth / 2) / Pages.Width : .5;
        _zoom = ResolveZoom(anchorPage, viewportWidth, viewportHeight);
        _tops = new double[pdf.PageCount + 1];
        double top = Edge, widest = 0;
        for (var page = 1; page <= pdf.PageCount; page++)
        {
            var (width, height) = PageDips(page, _zoom);
            _tops[page - 1] = top; top += height + Gap; widest = Math.Max(widest, width);
        }
        _tops[pdf.PageCount] = top;
        Pages.Width = Math.Max(viewportWidth, widest + Edge * 2);
        Pages.Height = Math.Max(viewportHeight, top - Gap + Edge);
        foreach (var element in _realized.Values) Place(element);

        var anchorHeight = _tops[anchorPage] - _tops[anchorPage - 1] - Gap;
        var target = Math.Max(0, fraction == 0 ? _tops[anchorPage - 1] - Edge / 2 : _tops[anchorPage - 1] + fraction * anchorHeight);
        // The scroller must see the new extent before it will accept an offset inside it.
        Scroller.UpdateLayout();
        Scroller.ChangeView(Math.Max(0, horizontal * Pages.Width - viewportWidth / 2), target, null, true);
        UpdateView(Math.Min(target, Math.Max(0, Pages.Height - viewportHeight)));
    }

    private void LayoutPresentation(double viewportWidth, double viewportHeight)
    {
        Pages.Width = viewportWidth; Pages.Height = viewportHeight;
        foreach (var page in _realized.Keys.Where(page => page != _page).ToList()) Unrealize(page);
        var element = Realize(_page);
        var (width, height) = PageDips(_page, 1);
        var zoom = Math.Min(viewportWidth / width, viewportHeight / height);
        element.Root.Width = width * zoom; element.Root.Height = height * zoom;
        Canvas.SetLeft(element.Root, (viewportWidth - width * zoom) / 2); Canvas.SetTop(element.Root, (viewportHeight - height * zoom) / 2);
        RedrawMarks(element); PumpRenders();
    }

    private int PageAt(double y)
    {
        int low = 0, high = _tops.Length - 2;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_tops[middle] <= y) low = middle; else high = middle - 1;
        }
        return low + 1;
    }

    private void Place(PageElement element)
    {
        var (width, height) = PageDips(element.Page, _zoom);
        element.Root.Width = width; element.Root.Height = height;
        Canvas.SetLeft(element.Root, Math.Max(Edge, (Pages.Width - width) / 2)); Canvas.SetTop(element.Root, _tops[element.Page - 1]);
        RedrawMarks(element);
    }

    private PageElement Realize(int page)
    {
        if (_realized.TryGetValue(page, out var existing)) return existing;
        var element = new PageElement(page);
        element.Root.Children.Add(new Border { Background = new SolidColorBrush(Colors.White) });
        element.Root.Children.Add(element.Image);
        element.Root.Children.Add(element.Marks);
        element.Root.Children.Add(element.Notes);
        element.Root.Children.Add(new Border { BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 0, 0, 0)), BorderThickness = new Thickness(1), IsHitTestVisible = false });
        _realized[page] = element; Pages.Children.Add(element.Root);
        if (!_presentation) Place(element);
        _ = AttachTextAsync(element);
        return element;
    }

    private void Unrealize(int page)
    {
        if (_realized.Remove(page, out var element)) Pages.Children.Remove(element.Root);
    }

    private async Task AttachTextAsync(PageElement element)
    {
        var text = await LoadTextAsync(element.Page);
        if (text is null || !_realized.TryGetValue(element.Page, out var current) || !ReferenceEquals(current, element)) return;
        element.Text = text; RedrawMarks(element);
    }

    /// <summary>Realizes the pages around the viewport and works out which page the reader is on.</summary>
    private void UpdateView(double? offsetOverride = null)
    {
        if (_pdf is not { } pdf || _tops.Length == 0 || _presentation) return;
        double offset = offsetOverride ?? Scroller.VerticalOffset, viewportHeight = Scroller.ActualHeight;
        int first = Math.Max(1, PageAt(offset) - 1), last = Math.Min(pdf.PageCount, PageAt(offset + viewportHeight) + 1);
        foreach (var page in _realized.Keys.Where(page => page < first || page > last).ToList()) Unrealize(page);
        for (var page = first; page <= last; page++) Realize(page);

        // The current page is the one showing the most of itself; ties go to the earlier page.
        var best = _page; double bestVisible = -1;
        for (var page = first; page <= last; page++)
        {
            var visible = Math.Min(_tops[page] - Gap, offset + viewportHeight) - Math.Max(_tops[page - 1], offset);
            if (visible > bestVisible + .5) { bestVisible = visible; best = page; }
        }
        if (best != _page) { _page = best; RaiseState(); }
        PumpRenders();
    }

    private void RaiseState() { if (_pdf is not null) StateChanged?.Invoke(new(_page, _zoom, _scale, _rotation)); }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateView();

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pdf is null) return;
        var (page, fraction) = _pendingAnchor ?? CaptureAnchor();
        var zoom = _zoom; Relayout(page, fraction);
        if (zoom != _zoom) RaiseState();
    }

    // ---- Rendering ----------------------------------------------------------------------

    private (int Width, int Height) TargetPixels(PageElement element)
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        double width = element.Root.Width * scale, height = element.Root.Height * scale;
        // Past the pixel budget the page is rendered smaller and stretched, trading sharpness for memory.
        if (width * height > MaximumPagePixels) { var shrink = Math.Sqrt(MaximumPagePixels / (width * height)); width *= shrink; height *= shrink; }
        return (Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)));
    }

    private bool NeedsRender(PageElement element)
    {
        if (element.Failed || double.IsNaN(element.Root.Width)) return false;
        var (width, height) = TargetPixels(element);
        return element.RenderedWidth != width || element.RenderedHeight != height || element.RenderedRotation != _rotation;
    }

    /// <summary>Renders stale pages one at a time off the UI thread, nearest to the current page first.</summary>
    private async void PumpRenders()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_pdf is { } pdf)
            {
                var element = _realized.Values.Where(NeedsRender).OrderBy(candidate => Math.Abs(candidate.Page - _page)).FirstOrDefault();
                if (element is null) break;
                var (width, height) = TargetPixels(element);
                int rotation = _rotation, generation = _generation, page = element.Page, bytes = width * height * 4;
                var buffer = ArrayPool<byte>.Shared.Rent(bytes);
                try
                {
                    await Task.Run(() => pdf.Render(page, rotation, width, height, buffer));
                    if (generation != _generation) continue;
                    // Record the attempt even if the element scrolled away, so a stale element is never retried forever.
                    element.RenderedWidth = width; element.RenderedHeight = height; element.RenderedRotation = rotation;
                    var bitmap = new WriteableBitmap(width, height);
                    using (var pixels = bitmap.PixelBuffer.AsStream()) pixels.Write(buffer, 0, bytes);
                    bitmap.Invalidate(); element.Image.Source = bitmap;
                }
                catch (ObjectDisposedException) { break; }
                catch (DocumentException ex)
                {
                    element.Failed = true;
                    if (generation == _generation && !_warned) { _warned = true; Warning?.Invoke(ex.Message); }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        finally { _pumping = false; }
    }

    private Task<PdfPageText?> LoadTextAsync(int page)
    {
        if (_text.TryGetValue(page, out var cached)) return cached;
        if (_pdf is not { } pdf) return Task.FromResult<PdfPageText?>(null);
        var generation = _generation;
        var task = Load();
        _text[page] = task; _textOrder.Enqueue(page);
        // Text for pages the reader has left is cheap to read again, so only a window of it is kept.
        while (_textOrder.Count > 64)
        {
            var old = _textOrder.Dequeue();
            if (_realized.ContainsKey(old)) { _textOrder.Enqueue(old); break; }
            _text.Remove(old);
        }
        return task;

        async Task<PdfPageText?> Load()
        {
            try { var text = await Task.Run(() => ReadText(pdf, page)); return generation == _generation ? text : null; }
            catch (Exception ex) when (ex is ObjectDisposedException or DocumentException) { return null; }
        }
    }

    /// <summary>Renders a small unrotated preview of a page, or null when the page cannot be drawn.</summary>
    public async Task<WriteableBitmap?> RenderThumbnailAsync(int page)
    {
        if (_pdf is not { } pdf || page < 1 || page > pdf.PageCount) return null;
        var size = pdf.GetPageSize(page); var generation = _generation;
        var fit = Math.Min(256 / size.Width, 332 / size.Height);
        int width = Math.Max(1, (int)(size.Width * fit)), height = Math.Max(1, (int)(size.Height * fit));
        try
        {
            var buffer = await Task.Run(() => { var pixels = new byte[width * height * 4]; pdf.Render(page, 0, width, height, pixels); return pixels; });
            if (generation != _generation) return null;
            var bitmap = new WriteableBitmap(width, height);
            using (var pixels = bitmap.PixelBuffer.AsStream()) pixels.Write(buffer, 0, buffer.Length);
            bitmap.Invalidate(); return bitmap;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DocumentException) { return null; }
    }

    // ---- Commands -----------------------------------------------------------------------

    public void GoToPage(int page)
    {
        if (_pdf is not { } pdf) return;
        page = Math.Clamp(page, 1, pdf.PageCount);
        if (_presentation) { if (page != _page) { _page = page; Relayout(page, 0); RaiseState(); } return; }
        if (_tops.Length == 0) { _pendingAnchor = (page, 0); return; }
        var target = Math.Max(0, _tops[page - 1] - Edge / 2);
        Scroller.ChangeView(null, target, null, true);
        UpdateView(Math.Min(target, Math.Max(0, Pages.Height - Scroller.ActualHeight)));
    }

    public void SetScale(string scale)
    {
        if (_pdf is null || _presentation) return;
        var (page, fraction) = CaptureAnchor();
        _scale = NormalizeScale(scale); Relayout(page, fraction); RaiseState();
    }

    public void Rotate()
    {
        if (_pdf is null) return;
        var page = _page;
        _rotation = (_rotation + 90) % 360;
        // The old bitmap has the wrong aspect for the turned page, so drop it rather than stretch it.
        foreach (var element in _realized.Values) element.Image.Source = null;
        Relayout(page, 0); RaiseState();
    }

    public void SetPresentation(bool enabled)
    {
        if (_pdf is null || enabled == _presentation) return;
        var page = _page;
        if (enabled) SetTool(AnnotationTool.None);
        _presentation = enabled; ApplyPresentationChrome();
        if (!enabled) { foreach (var key in _realized.Keys.ToList()) Unrealize(key); }
        Relayout(page, 0);
        Focus(FocusState.Programmatic);
    }

    private void ApplyPresentationChrome()
    {
        ExitPresentationButton.Visibility = _presentation ? Visibility.Visible : Visibility.Collapsed;
        Scroller.VerticalScrollBarVisibility = Scroller.HorizontalScrollBarVisibility = _presentation ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        PresentationBackdrop.Visibility = ExitPresentationButton.Visibility;
    }

    private void ExitPresentation_Click(object sender, RoutedEventArgs e) => ExitPresentationRequested?.Invoke();
}
