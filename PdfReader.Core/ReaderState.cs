using System.Globalization;
namespace PdfReader.Core;
public sealed class ReaderState
{
    public int PageCount { get; private set; }
    public int Page { get; private set; } = 1;
    public double Zoom { get; private set; } = 1;
    public string Scale { get; private set; } = "page-width";
    public int Rotation { get; private set; }
    public bool IsOpen => PageCount > 0;
    public void Open(int pageCount)
    {
        if (pageCount is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(pageCount));
        PageCount = pageCount; Page = 1;
    }
    public int Navigate(int page) => Page = Math.Clamp(page, 1, Math.Max(1, PageCount));
    public double SetZoom(double zoom)
    {
        Zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, .25, 5) : 1;
        Scale = Zoom.ToString(CultureInfo.InvariantCulture);
        return Zoom;
    }
    public void SetScale(string scale)
    {
        if (scale is "page-width" or "page-fit" or "auto") Scale = scale;
        else if (double.TryParse(scale, CultureInfo.InvariantCulture, out var value)) SetZoom(value);
    }
    public void Update(int page, double zoom, string scale, int rotation)
    {
        Navigate(page); SetScale(scale);
        Zoom = double.IsFinite(zoom) && zoom > 0 ? zoom : 1;
        Rotation = ((rotation % 360) + 360) % 360;
    }
    public void Reset() { PageCount = 0; Page = 1; Zoom = 1; Scale = "page-width"; Rotation = 0; }
}
public static class PrintRange
{
    public const int MaximumPages = 100;
    public static int[] Parse(string text, int pageCount)
    {
        var pages = new SortedSet<int>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length is < 1 or > 2 || !int.TryParse(ends[0], out var first)) throw new FormatException("Use page numbers or ranges, for example 1-5, 8.");
            var last = first;
            if (ends.Length == 2 && !int.TryParse(ends[1], out last)) throw new FormatException("Enter a valid last page.");
            if (first < 1 || last < first || last > pageCount) throw new FormatException($"Choose pages between 1 and {pageCount}.");
            if ((long)last - first + 1 > MaximumPages) throw new FormatException("Print up to 100 pages per batch.");
            for (var page = first; page <= last; page++) pages.Add(page);
            if (pages.Count > MaximumPages) throw new FormatException("Print up to 100 pages per batch.");
        }
        if (pages.Count == 0) throw new FormatException("Enter at least one page to print.");
        return pages.ToArray();
    }
}
