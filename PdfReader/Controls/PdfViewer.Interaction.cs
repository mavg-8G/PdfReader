using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PdfReader.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace PdfReader.Controls;

// Selection, search, annotations and input for the viewer. All page coordinates here are fractions of
// the unrotated page ("canonical"), converted to the rotated view only when drawing or hit-testing.
public sealed partial class PdfViewer
{
    private const int MaximumAnnotations = 500, MaximumStrokePoints = 1024, MaximumHighlightRects = 100, MaximumMatches = 20_000, MaximumCopyLength = 4_000_000;
    private readonly record struct TextPosition(int Page, int Index);
    private readonly record struct Match(int Page, int Start, int Length);

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _markupTimer;
    private DocumentMarkup _markup = DocumentMarkup.Empty("");
    private AnnotationTool _tool;
    private TextPosition? _anchor, _focus;
    private bool _dragging, _moved;
    private (int Page, double X, double Y) _press;
    private (int Page, List<DocumentMarkupPoint> Points)? _stroke;
    private Polyline? _strokeLine;

    private readonly List<Match> _matches = [];
    private readonly Dictionary<int, List<int>> _matchesByPage = [];
    private CancellationTokenSource? _searchCancel;
    private int _currentMatch = -1;
    private string _query = "";
    private bool _queryCaseSensitive, _searchPending;

    // ---- Coordinate helpers -------------------------------------------------------------

    private static (double X, double Y) ToCanonical(double x, double y, int rotation) => rotation switch { 90 => (y, 1 - x), 180 => (1 - x, 1 - y), 270 => (1 - y, x), _ => (x, y) };
    private static (double X, double Y) ToDisplayed(double x, double y, int rotation) => rotation switch { 90 => (1 - y, x), 180 => (1 - x, 1 - y), 270 => (y, 1 - x), _ => (x, y) };
    private static PdfRect ToDisplayed(PdfRect value, int rotation) => rotation switch
    {
        90 => new(1 - value.Y - value.Height, value.X, value.Height, value.Width),
        180 => new(1 - value.X - value.Width, 1 - value.Y - value.Height, value.Width, value.Height),
        270 => new(value.Y, 1 - value.X - value.Width, value.Height, value.Width),
        _ => value
    };

    /// <summary>Finds the page under a point in the Pages canvas, optionally snapping to the nearest page.</summary>
    private bool TryLocate(Point position, bool snap, out PageElement element, out double x, out double y)
    {
        element = null!; x = y = 0; PageElement? nearest = null; var distance = double.MaxValue;
        foreach (var candidate in _realized.Values)
        {
            double left = Canvas.GetLeft(candidate.Root), top = Canvas.GetTop(candidate.Root), width = candidate.Root.Width, height = candidate.Root.Height;
            if (double.IsNaN(width) || width <= 0) continue;
            var inside = position.X >= left && position.X <= left + width && position.Y >= top && position.Y <= top + height;
            var gap = inside ? 0 : Math.Abs(position.Y - Math.Clamp(position.Y, top, top + height)) + Math.Abs(position.X - Math.Clamp(position.X, left, left + width));
            if (gap < distance) { distance = gap; nearest = candidate; }
        }
        if (nearest is null || (distance > 0 && !snap)) return false;
        element = nearest;
        var localX = Math.Clamp((position.X - Canvas.GetLeft(nearest.Root)) / nearest.Root.Width, 0, 1);
        var localY = Math.Clamp((position.Y - Canvas.GetTop(nearest.Root)) / nearest.Root.Height, 0, 1);
        (x, y) = ToCanonical(localX, localY, _rotation);
        return true;
    }

    // ---- Drawing the overlays -----------------------------------------------------------

    private void RedrawAll() { foreach (var element in _realized.Values) RedrawMarks(element); }

    private void RedrawMarks(PageElement element)
    {
        element.Marks.Children.Clear(); element.Notes.Children.Clear();
        double width = element.Root.Width, height = element.Root.Height;
        if (double.IsNaN(width) || width <= 0) return;

        void Fill(PdfRect canonical, Color color)
        {
            var rect = ToDisplayed(canonical, _rotation);
            var shape = new Rectangle { Width = rect.Width * width, Height = rect.Height * height, Fill = new SolidColorBrush(color), RadiusX = 1.5, RadiusY = 1.5 };
            Canvas.SetLeft(shape, rect.X * width); Canvas.SetTop(shape, rect.Y * height);
            element.Marks.Children.Add(shape);
        }

        foreach (var annotation in _markup.Annotations)
        {
            if (annotation.Page != element.Page) continue;
            switch (annotation.Type)
            {
                case "highlight":
                    foreach (var rect in annotation.Rects) Fill(new(rect.X, rect.Y, rect.Width, rect.Height), ColorHelper.FromArgb(110, 255, 228, 92));
                    break;
                case "drawing":
                    element.Marks.Children.Add(NewStroke(annotation.Points, width, height));
                    break;
                case "note":
                    AddNote(element, annotation, width, height);
                    break;
            }
        }
        if (element.Text is not { } text) return;
        if (_matchesByPage.TryGetValue(element.Page, out var matches))
            foreach (var index in matches)
                foreach (var rect in text.GetRects(_matches[index].Start, _matches[index].Length))
                    Fill(rect, index == _currentMatch ? ColorHelper.FromArgb(150, 255, 150, 50) : ColorHelper.FromArgb(100, 255, 214, 0));
        if (SelectionOn(element.Page, text) is var (from, to))
        {
            var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color ? color : Colors.DodgerBlue;
            foreach (var rect in text.GetRects(from, to - from + 1)) Fill(rect, Color.FromArgb(90, accent.R, accent.G, accent.B));
        }
    }

    private Polyline NewStroke(IEnumerable<DocumentMarkupPoint> points, double width, double height)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 216, 59, 59)), StrokeThickness = Math.Max(1.5, Math.Min(width, height) * .004),
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };
        foreach (var point in points) { var (x, y) = ToDisplayed(point.X, point.Y, _rotation); line.Points.Add(new Point(x * width, y * height)); }
        return line;
    }

    private void AddNote(PageElement element, DocumentMarkupAnnotation annotation, double width, double height)
    {
        var fill = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 228, 92)); var edge = new SolidColorBrush(ColorHelper.FromArgb(255, 139, 106, 0));
        var button = new Button { Width = 24, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), Content = new FontIcon { Glyph = "", FontSize = 11, Foreground = edge } };
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" }) button.Resources[key] = fill;
        foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" }) button.Resources[key] = edge;
        ToolTipService.SetToolTip(button, annotation.Text.Length > 200 ? annotation.Text[..200] + "…" : annotation.Text);
        AutomationProperties.SetName(button, T("Open note", "Abrir nota"));

        var input = new TextBox { Text = annotation.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 5000, Width = 260, MinHeight = 88, MaxHeight = 240 };
        AutomationProperties.SetName(input, T("Note text", "Texto de la nota"));
        var remove = new Button { Content = T("Delete note", "Eliminar nota"), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(input); panel.Children.Add(remove);
        var flyout = new Flyout { Content = panel };
        input.TextChanged += (_, _) => { annotation.Text = input.Text; ScheduleMarkup(); };
        remove.Click += (_, _) => { flyout.Hide(); _markup.Annotations.Remove(annotation); RedrawAll(); ScheduleMarkup(); };
        button.Flyout = flyout;

        var (x, y) = ToDisplayed(annotation.X, annotation.Y, _rotation);
        Canvas.SetLeft(button, x * width - 12); Canvas.SetTop(button, y * height - 12);
        element.Notes.Children.Add(button);
    }

    // ---- Selection ----------------------------------------------------------------------

    public bool HasSelection => _anchor is not null && _focus is not null;

    private (TextPosition Start, TextPosition End)? OrderedSelection()
    {
        if (_anchor is not { } anchor || _focus is not { } focus) return null;
        return anchor.Page < focus.Page || (anchor.Page == focus.Page && anchor.Index <= focus.Index) ? (anchor, focus) : (focus, anchor);
    }

    // Inclusive character range selected on one page, or null.
    private (int From, int To)? SelectionOn(int page, PdfPageText text)
    {
        if (OrderedSelection() is not var (start, end) || page < start.Page || page > end.Page || text.Text.Length == 0) return null;
        int from = page == start.Page ? start.Index : 0, to = Math.Min(text.Text.Length - 1, page == end.Page ? end.Index : int.MaxValue);
        return from <= to ? (from, to) : null;
    }

    private void ClearSelection()
    {
        if (_anchor is null && _focus is null) return;
        _anchor = _focus = null; RedrawAll();
    }

    public void SelectAll()
    {
        if (_pdf is not { } pdf) return;
        _anchor = new(1, 0); _focus = new(pdf.PageCount, int.MaxValue); RedrawAll();
    }

    private async Task<string> SelectedTextAsync()
    {
        if (OrderedSelection() is not var (start, end)) return "";
        var builder = new StringBuilder();
        for (var page = start.Page; page <= end.Page && builder.Length < MaximumCopyLength; page++)
        {
            if (await LoadTextAsync(page) is not { } text || SelectionOn(page, text) is not var (from, to)) continue;
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(text.Text, from, to - from + 1);
        }
        return builder.Length > MaximumCopyLength ? builder.ToString(0, MaximumCopyLength) : builder.ToString();
    }

    public async Task CopySelectionAsync()
    {
        if (_pdf is not { } pdf || !HasSelection) return;
        if (!pdf.CanCopy) { Warning?.Invoke("Copying is restricted by this document’s permissions."); return; }
        var text = await SelectedTextAsync();
        if (text.Length == 0) return;
        var package = new DataPackage(); package.SetText(text);
        try { Clipboard.SetContent(package); Clipboard.Flush(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { Warning?.Invoke("The clipboard is busy. Try copying again."); }
    }

    // ---- Annotations --------------------------------------------------------------------

    public AnnotationTool Tool => _tool;
    public string PendingNoteText { get; set; } = "";

    public void SetTool(AnnotationTool tool)
    {
        if (_tool == tool) return;
        _tool = tool; UpdateCursor(false); ToolChanged?.Invoke();
    }

    /// <summary>A detached copy of the markup, safe to serialize while the reader keeps editing.</summary>
    public DocumentMarkup SnapshotMarkup() => JsonSerializer.Deserialize<DocumentMarkup>(JsonSerializer.Serialize(_markup))!;

    private void ScheduleMarkup() { _markupTimer.Stop(); _markupTimer.Start(); }

    public void FlushMarkup()
    {
        if (!_markupTimer.IsRunning) return;
        _markupTimer.Stop(); MarkupChanged?.Invoke();
    }

    private bool CanAnnotate()
    {
        if (_markup.Annotations.Count < MaximumAnnotations) return true;
        Warning?.Invoke("This document has reached the limit of 500 annotations."); return false;
    }

    /// <summary>Turns the selected text into highlights. Returns false when there was nothing to highlight.</summary>
    public async Task<bool> HighlightSelectionAsync()
    {
        if (OrderedSelection() is not var (start, end)) return false;
        var added = false;
        for (var page = start.Page; page <= end.Page && CanAnnotate(); page++)
        {
            if (await LoadTextAsync(page) is not { } text || SelectionOn(page, text) is not var (from, to)) continue;
            var rects = text.GetRects(from, to - from + 1).Take(MaximumHighlightRects).Select(rect => new DocumentMarkupRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height }).ToList();
            if (rects.Count == 0) continue;
            _markup.Annotations.Add(new DocumentMarkupAnnotation { Id = Guid.NewGuid().ToString("N"), Type = "highlight", Page = page, Rects = rects });
            added = true;
        }
        if (!added) return false;
        _anchor = _focus = null; RedrawAll(); ScheduleMarkup();
        return true;
    }

    private DocumentMarkupAnnotation? MarkAt(int page, double x, double y)
    {
        foreach (var annotation in _markup.Annotations.AsEnumerable().Reverse())
        {
            if (annotation.Page != page) continue;
            if (annotation.Type == "highlight" && annotation.Rects.Any(rect => x >= rect.X && x <= rect.X + rect.Width && y >= rect.Y && y <= rect.Y + rect.Height)) return annotation;
            if (annotation.Type == "drawing" && annotation.Points.Any(point => Math.Abs(point.X - x) < .015 && Math.Abs(point.Y - y) < .015)) return annotation;
        }
        return null;
    }

    // ---- Pointer input ------------------------------------------------------------------

    private void UpdateCursor(bool overText) =>
        ProtectedCursor = InputSystemCursor.Create(_tool != AnnotationTool.None ? InputSystemCursorShape.Cross : overText ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Arrow);

    private void Pages_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pdf is null) return;
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(Pages);
        if (!point.Properties.IsLeftButtonPressed) return;
        // Touch belongs to the scroller for panning unless an annotation tool is waiting for a tap.
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch && _tool == AnnotationTool.None) return;
        if (!TryLocate(point.Position, false, out var element, out var x, out var y)) { ClearSelection(); return; }

        if (_tool == AnnotationTool.Note)
        {
            if (CanAnnotate())
            {
                var text = PendingNoteText.Trim();
                _markup.Annotations.Add(new DocumentMarkupAnnotation { Id = Guid.NewGuid().ToString("N"), Type = "note", Page = element.Page, X = x, Y = y, Text = text.Length > 0 ? text[..Math.Min(text.Length, 5000)] : T("Sticky note", "Nota adhesiva") });
                RedrawMarks(element); ScheduleMarkup();
            }
            SetTool(AnnotationTool.None); e.Handled = true; return;
        }
        if (_tool == AnnotationTool.Draw)
        {
            if (!CanAnnotate()) { SetTool(AnnotationTool.None); return; }
            _stroke = (element.Page, [new DocumentMarkupPoint { X = x, Y = y }]);
            _strokeLine = NewStroke(_stroke.Value.Points, element.Root.Width, element.Root.Height);
            element.Marks.Children.Add(_strokeLine);
            Pages.CapturePointer(e.Pointer); e.Handled = true; return;
        }

        _press = (element.Page, x, y); _dragging = true; _moved = false;
        var hadSelection = HasSelection;
        _focus = null;
        _anchor = element.Text is { } pageText && pageText.IsOverText(x, y) ? new(element.Page, pageText.CharacterAt(x, y)) : null;
        if (hadSelection) RedrawAll();
        Pages.CapturePointer(e.Pointer);
    }

    private void Pages_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pdf is null) return;
        var position = e.GetCurrentPoint(Pages).Position;
        if (_stroke is var (strokePage, points) && _strokeLine is { } line)
        {
            if (!_realized.TryGetValue(strokePage, out var target) || !TryLocate(position, true, out var located, out var sx, out var sy) || located.Page != strokePage) return;
            var previous = points[^1];
            if (points.Count >= MaximumStrokePoints || Math.Sqrt(Math.Pow(sx - previous.X, 2) + Math.Pow(sy - previous.Y, 2)) < .0015) return;
            points.Add(new DocumentMarkupPoint { X = sx, Y = sy });
            var (dx, dy) = ToDisplayed(sx, sy, _rotation); line.Points.Add(new Point(dx * target.Root.Width, dy * target.Root.Height));
            return;
        }
        if (!TryLocate(position, _dragging, out var element, out var x, out var y)) { if (!_dragging) UpdateCursor(false); return; }
        if (!_dragging) { UpdateCursor(element.Text?.IsOverText(x, y) == true); return; }
        if (_anchor is null || element.Text is not { } text) return;
        var index = text.CharacterAt(x, y);
        if (index < 0) return;
        var focus = new TextPosition(element.Page, index);
        if (focus == _focus) return;
        // A press that never leaves its first character is a click, not a one-letter selection.
        if (_focus is null && focus == _anchor) return;
        _focus = focus; _moved = true; RedrawAll();
    }

    private async void Pages_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_stroke is var (strokePage, points))
        {
            _stroke = null; _strokeLine = null; Pages.ReleasePointerCapture(e.Pointer);
            if (points.Count > 1) { _markup.Annotations.Add(new DocumentMarkupAnnotation { Id = Guid.NewGuid().ToString("N"), Type = "drawing", Page = strokePage, Points = points }); ScheduleMarkup(); }
            if (_realized.TryGetValue(strokePage, out var drawn)) RedrawMarks(drawn);
            return;
        }
        if (!_dragging) return;
        _dragging = false; Pages.ReleasePointerCapture(e.Pointer);
        if (_moved || _pdf is not { } pdf) return;
        // A plain click on an internal link follows it.
        var (page, x, y) = _press; var generation = _generation;
        try
        {
            var target = await Task.Run(() => pdf.GetLinkTarget(page, x, y));
            if (target is { } destination && generation == _generation) GoToPage(destination);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DocumentException) { }
    }

    private void Pages_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_pdf is null || _presentation || !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        e.Handled = true;
        var delta = e.GetCurrentPoint(Pages).Properties.MouseWheelDelta;
        if (delta != 0) SetScale((_zoom * (delta > 0 ? 1.1 : 1 / 1.1)).ToString(CultureInfo.InvariantCulture));
    }

    private void Pages_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_pdf is null || _tool != AnnotationTool.None || !TryLocate(e.GetPosition(Pages), false, out var element, out var x, out var y)) return;
        if (element.Text is not { } text || !text.IsOverText(x, y)) return;
        var index = text.CharacterAt(x, y);
        if (index < 0 || !char.IsLetterOrDigit(text.Text[index])) return;
        int from = index, to = index;
        while (from > 0 && char.IsLetterOrDigit(text.Text[from - 1])) from--;
        while (to < text.Text.Length - 1 && char.IsLetterOrDigit(text.Text[to + 1])) to++;
        _anchor = new(element.Page, from); _focus = new(element.Page, to); RedrawAll();
    }

    private void Pages_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (_pdf is not { } pdf) return;
        var menu = new MenuFlyout();
        void Add(string english, string spanish, Symbol icon, bool enabled, Action action, string? shortcut = null)
        {
            var item = new MenuFlyoutItem { Text = T(english, spanish), Icon = new SymbolIcon(icon), IsEnabled = enabled };
            if (shortcut is not null) item.KeyboardAcceleratorTextOverride = shortcut;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Copy", "Copiar", Symbol.Copy, HasSelection && pdf.CanCopy, () => _ = CopySelectionAsync(), "Ctrl+C");
        Add("Select all", "Seleccionar todo", Symbol.SelectAll, true, SelectAll, "Ctrl+A");
        Add("Highlight selection", "Resaltar selección", Symbol.Highlight, HasSelection, () => _ = HighlightSelectionAsync());
        var hasPoint = e.TryGetPosition(Pages, out var position);
        if (hasPoint && TryLocate(position, false, out var element, out var x, out var y) && MarkAt(element.Page, x, y) is { } mark)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var highlight = mark.Type == "highlight";
            Add(highlight ? "Remove highlight" : "Remove drawing", highlight ? "Quitar resaltado" : "Quitar dibujo", Symbol.Delete, true, () => { _markup.Annotations.Remove(mark); RedrawAll(); ScheduleMarkup(); });
        }
        if (hasPoint) menu.ShowAt(Pages, position); else menu.ShowAt(this);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (_pdf is not { } pdf || e.Handled) return;
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool control = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift);
        void Scroll(double horizontal, double vertical) => Scroller.ChangeView(Scroller.HorizontalOffset + horizontal, Scroller.VerticalOffset + vertical, null, true);
        var handled = true;
        switch (e.Key)
        {
            case VirtualKey.C when control: _ = CopySelectionAsync(); break;
            case VirtualKey.A when control: SelectAll(); break;
            case VirtualKey.Home: GoToPage(1); break;
            case VirtualKey.End: GoToPage(pdf.PageCount); break;
            case VirtualKey.Down when _presentation: case VirtualKey.Right when _presentation: case VirtualKey.Space when _presentation && !shift: GoToPage(_page + 1); break;
            case VirtualKey.Up when _presentation: case VirtualKey.Left when _presentation: case VirtualKey.Space when _presentation: GoToPage(_page - 1); break;
            case VirtualKey.Down: Scroll(0, 60); break;
            case VirtualKey.Up: Scroll(0, -60); break;
            case VirtualKey.Right: Scroll(60, 0); break;
            case VirtualKey.Left: Scroll(-60, 0); break;
            case VirtualKey.Space: Scroll(0, Scroller.ActualHeight * (shift ? -.9 : .9)); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    // ---- Search -------------------------------------------------------------------------

    /// <summary>Starts a search, or with <paramref name="again"/> moves to the next or previous match of the same query.</summary>
    public void Find(string query, bool caseSensitive, bool again, bool previous)
    {
        if (_pdf is not { } pdf) return;
        query = query.Length > 500 ? query[..500] : query;
        if (again && query == _query && caseSensitive == _queryCaseSensitive)
        {
            if (_matches.Count == 0) return;
            _currentMatch = ((_currentMatch < 0 ? 0 : _currentMatch + (previous ? -1 : 1)) + _matches.Count) % _matches.Count;
            RedrawAll(); RaiseSearch(); _ = RevealMatchAsync();
            return;
        }
        _searchCancel?.Cancel(); _matches.Clear(); _matchesByPage.Clear(); _currentMatch = -1;
        _query = query; _queryCaseSensitive = caseSensitive; _searchPending = !string.IsNullOrWhiteSpace(query);
        RedrawAll(); RaiseSearch();
        if (!_searchPending) return;

        var cancel = _searchCancel = new CancellationTokenSource(); var startPage = _page; var queue = DispatcherQueue;
        _ = Task.Run(() =>
        {
            var batch = new List<Match>(); var total = 0;
            for (var page = 1; page <= pdf.PageCount; page++)
            {
                if (cancel.IsCancellationRequested) return;
                try { foreach (var (start, length) in PdfSearch.Find(pdf.GetText(page).Text, query, caseSensitive)) { batch.Add(new(page, start, length)); total++; } }
                catch (ObjectDisposedException) { return; }
                catch (DocumentException) { }
                var done = page == pdf.PageCount || total >= MaximumMatches;
                if (done || page % 20 == 0)
                {
                    var chunk = batch.ToArray(); batch.Clear();
                    queue.TryEnqueue(() => { if (ReferenceEquals(cancel, _searchCancel) && !cancel.IsCancellationRequested) AppendMatches(chunk, done, startPage); });
                }
                if (done) return;
            }
        });
    }

    private void AppendMatches(Match[] chunk, bool done, int startPage)
    {
        var touched = false;
        foreach (var match in chunk)
        {
            if (!_matchesByPage.TryGetValue(match.Page, out var list)) _matchesByPage[match.Page] = list = [];
            list.Add(_matches.Count); _matches.Add(match);
            touched |= _realized.ContainsKey(match.Page);
        }
        _searchPending = !done;
        if (_currentMatch < 0 && _matches.Count > 0)
        {
            // Start from the page being read; wrap to the first match only once the whole document is known.
            var index = _matches.FindIndex(match => match.Page >= startPage);
            if (index < 0 && done) index = 0;
            if (index >= 0) { _currentMatch = index; touched = true; _ = RevealMatchAsync(); }
        }
        if (touched) RedrawAll();
        RaiseSearch();
    }

    private void RaiseSearch() => SearchChanged?.Invoke(new(_currentMatch + 1, _matches.Count, _searchPending));

    private async Task RevealMatchAsync()
    {
        if (_currentMatch < 0 || _currentMatch >= _matches.Count) return;
        var match = _matches[_currentMatch];
        if (_presentation) { GoToPage(match.Page); return; }
        var text = await LoadTextAsync(match.Page);
        if (_pdf is null || _tops.Length == 0 || _currentMatch < 0 || _currentMatch >= _matches.Count || _matches[_currentMatch] != match) return;
        var rect = text?.GetRects(match.Start, match.Length).Select(value => ToDisplayed(value, _rotation)).FirstOrDefault() ?? default;
        var (width, height) = PageDips(match.Page, _zoom);
        double y = _tops[match.Page - 1] + rect.Y * height, x = Math.Max(Edge, (Pages.Width - width) / 2) + rect.X * width;
        double offset = Scroller.VerticalOffset, viewportHeight = Scroller.ActualHeight, viewportWidth = Scroller.ActualWidth;
        double? vertical = y < offset + 24 || y + rect.Height * height > offset + viewportHeight - 24 ? Math.Max(0, y - viewportHeight / 3) : null;
        double? horizontal = x < Scroller.HorizontalOffset || x + rect.Width * width > Scroller.HorizontalOffset + viewportWidth ? Math.Max(0, x - viewportWidth / 3) : null;
        if (vertical is null && horizontal is null) return;
        Scroller.ChangeView(horizontal, vertical, null, true);
        if (vertical is { } target) UpdateView(Math.Min(target, Math.Max(0, Pages.Height - viewportHeight)));
    }

    public void CloseFind()
    {
        _searchCancel?.Cancel(); _searchCancel = null;
        if (_matches.Count == 0 && _query.Length == 0) return;
        _matches.Clear(); _matchesByPage.Clear(); _currentMatch = -1; _query = ""; _searchPending = false;
        RedrawAll();
    }
}
