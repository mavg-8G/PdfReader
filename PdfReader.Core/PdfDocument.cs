using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static PdfReader.Core.PdfiumNative;

namespace PdfReader.Core;

/// <summary>A page size in PDF points (1/72 inch), as displayed before any reader rotation.</summary>
public readonly record struct PdfSize(double Width, double Height);

/// <summary>
/// A rectangle as a fraction of the page, measured from the top-left corner of the page as displayed
/// without reader rotation. This is the same space the markup sidecar file uses.
/// </summary>
public readonly record struct PdfRect(double X, double Y, double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public sealed class PdfPasswordException(bool incorrect) : Exception("This document is encrypted.")
{
    /// <summary>True when a password was supplied and PDFium rejected it.</summary>
    public bool Incorrect { get; } = incorrect;
}

/// <summary>Renders and reads a PDF through PDFium, reading bytes from the session's read-only lease.</summary>
public sealed unsafe class PdfDocument : IDisposable
{
    private const int MaximumPages = 100_000;
    private const int TransformScale = 1_000_000;
    private nint _document, _form, _formInfo;
    private PdfiumFileAccess* _access;
    private GCHandle _source;

    public int PageCount { get; }
    public bool CanPrint { get; }
    public bool CanCopy { get; }
    private readonly PdfSize[] _sizes;

    private PdfDocument(nint document, PdfiumFileAccess* access, GCHandle source)
    {
        _document = document; _access = access; _source = source;
        PageCount = FPDF_GetPageCount(document);
        if (PageCount < 1) throw Unreadable();
        if (PageCount > MaximumPages) throw new DocumentException("Unable to open this PDF", "This document exceeds the 100,000-page safety limit.");
        var permissions = FPDF_GetDocPermissions(document);
        CanPrint = (permissions & 4) != 0; CanCopy = (permissions & 16) != 0;
        _sizes = new PdfSize[PageCount];
        for (var index = 0; index < PageCount; index++)
        {
            // US Letter stands in for a page whose size cannot be read, so the rest of the document still lays out.
            if (FPDF_GetPageSizeByIndex(document, index, out var width, out var height) == 0 || !(width > 0) || !(height > 0)) { width = 612; height = 792; }
            _sizes[index] = new(width, height);
        }
        // A form environment with no callbacks is enough to draw filled-in fields; Folio never forwards input to it.
        _formInfo = (nint)NativeMemory.AllocZeroed(512);
        *(int*)_formInfo = 1;
        _form = FPDFDOC_InitFormFillEnvironment(document, _formInfo);
        if (_form != 0) FPDF_SetFormFieldHighlightAlpha(_form, 0);
    }

    /// <summary>Opens the document. Throws <see cref="PdfPasswordException"/> when a (different) password is needed.</summary>
    public static PdfDocument Open(DocumentSession session, string? password = null)
    {
        lock (Sync)
        {
            EnsureInitialized();
            var source = GCHandle.Alloc(session);
            var access = (PdfiumFileAccess*)NativeMemory.AllocZeroed((nuint)sizeof(PdfiumFileAccess));
            access->Length = checked((uint)session.Length); access->GetBlock = &ReadBlock; access->Parameter = GCHandle.ToIntPtr(source);
            var document = FPDF_LoadCustomDocument(access, password);
            if (document == 0)
            {
                var error = FPDF_GetLastError();
                NativeMemory.Free(access); source.Free();
                throw error == ErrorPassword ? new PdfPasswordException(password is not null) : Unreadable();
            }
            PdfDocument? result = null;
            try { return result = new PdfDocument(document, access, source); }
            finally { if (result is null) { FPDF_CloseDocument(document); NativeMemory.Free(access); source.Free(); } }
        }
    }

    public PdfSize GetPageSize(int page) => _sizes[page - 1];

    /// <summary>Renders a page into a top-down BGRA buffer of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    public void Render(int page, int rotation, int width, int height, byte[] buffer, bool forPrinting = false)
    {
        if (width < 1 || height < 1 || buffer.Length < (long)width * height * 4) throw new ArgumentException("The buffer is too small for the requested size.", nameof(buffer));
        var turns = ((rotation / 90 % 4) + 4) % 4;
        var flags = RenderAnnotations | (forPrinting ? RenderForPrinting : 0);
        WithPage(page, handle =>
        {
            fixed (byte* pixels = buffer)
            {
                var bitmap = FPDFBitmap_CreateEx(width, height, BitmapBgra, (nint)pixels, width * 4);
                if (bitmap == 0) throw new DocumentException("This PDF could not be read", "A page could not be rendered completely. The PDF may contain damaged or unsupported content.");
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(bitmap, handle, 0, 0, width, height, turns, flags);
                    if (_form != 0) FPDF_FFLDraw(_form, bitmap, handle, 0, 0, width, height, turns, flags);
                }
                finally { FPDFBitmap_Destroy(bitmap); }
            }
            return 0;
        });
    }

    /// <summary>Reads the page's characters and where each one sits on the page.</summary>
    public PdfPageText GetText(int page) => WithPage(page, handle =>
    {
        var text = FPDFText_LoadPage(handle);
        if (text == 0) return PdfPageText.Empty;
        try
        {
            var count = Math.Max(0, FPDFText_CountChars(text));
            if (count == 0) return PdfPageText.Empty;
            var transform = PageTransform.Read(handle);
            var characters = new char[count]; var boxes = new PdfRect[count];
            for (var index = 0; index < count; index++)
            {
                var code = FPDFText_GetUnicode(text, index);
                // One UTF-16 unit per PDFium character keeps string offsets and box indexes identical.
                characters[index] = code == 0 ? ' ' : code > 0xFFFF || char.IsSurrogate((char)code) ? '�' : (char)code;
                if (FPDFText_GetCharBox(text, index, out var left, out var right, out var bottom, out var top) != 0 && right > left && top > bottom)
                    boxes[index] = transform.ToDisplay(left, bottom, right, top);
            }
            return new PdfPageText(new string(characters), boxes);
        }
        finally { FPDFText_ClosePage(text); }
    });

    /// <summary>Returns the page an internal link at this point leads to, or null when there is none.</summary>
    public int? GetLinkTarget(int page, double x, double y) => WithPage(page, handle =>
    {
        if (FPDF_DeviceToPage(handle, 0, 0, TransformScale, TransformScale, 0, (int)(x * TransformScale), (int)(y * TransformScale), out var pageX, out var pageY) == 0) return (int?)null;
        var link = FPDFLink_GetLinkAtPoint(handle, pageX, pageY);
        if (link == 0) return null;
        var destination = FPDFLink_GetDest(_document, link);
        if (destination == 0)
        {
            var action = FPDFLink_GetAction(link);
            if (action != 0 && FPDFAction_GetType(action) == ActionGoTo) destination = FPDFAction_GetDest(_document, action);
        }
        if (destination == 0) return null;
        var index = FPDFDest_GetDestPageIndex(_document, destination);
        return index >= 0 && index < PageCount ? index + 1 : null;
    });

    private T WithPage<T>(int page, Func<nint, T> action)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_document == 0, this);
            var handle = FPDF_LoadPage(_document, page - 1);
            if (handle == 0) throw new DocumentException("This PDF could not be read", "A page could not be rendered completely. The PDF may contain damaged or unsupported content.");
            if (_form != 0) FORM_OnAfterLoadPage(handle, _form);
            try { return action(handle); }
            finally
            {
                if (_form != 0) FORM_OnBeforeClosePage(handle, _form);
                FPDF_ClosePage(handle);
            }
        }
    }

    private static DocumentException Unreadable() => new("This PDF could not be read", "The file appears damaged or incomplete. Try obtaining a new copy.");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadBlock(nint parameter, uint position, byte* buffer, uint size)
    {
        // An exception must never unwind into native code; PDFium treats 0 as a read failure.
        try
        {
            if (GCHandle.FromIntPtr(parameter).Target is not DocumentSession session) return 0;
            var target = new Span<byte>(buffer, checked((int)size)); var done = 0;
            while (done < target.Length)
            {
                var read = RandomAccess.Read(session.Source.SafeFileHandle, target[done..], position + done);
                if (read <= 0) return 0;
                done += read;
            }
            return 1;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_document == 0) return;
            if (_form != 0) { FPDFDOC_ExitFormFillEnvironment(_form); _form = 0; }
            FPDF_CloseDocument(_document); _document = 0;
            NativeMemory.Free((void*)_formInfo); _formInfo = 0;
            NativeMemory.Free(_access); _access = null;
            _source.Free();
        }
    }

    /// <summary>Maps PDF page space (points, bottom-left origin, any crop box or intrinsic rotation) to display fractions.</summary>
    private readonly record struct PageTransform(double A, double B, double C, double D, double E, double F)
    {
        public static PageTransform Read(nint page)
        {
            const double span = 1000;
            static (double X, double Y) Map(nint page, double x, double y)
            {
                FPDF_PageToDevice(page, 0, 0, TransformScale, TransformScale, 0, x, y, out var deviceX, out var deviceY);
                return ((double)deviceX / TransformScale, (double)deviceY / TransformScale);
            }
            var origin = Map(page, 0, 0); var alongX = Map(page, span, 0); var alongY = Map(page, 0, span);
            return new((alongX.X - origin.X) / span, (alongY.X - origin.X) / span, origin.X, (alongX.Y - origin.Y) / span, (alongY.Y - origin.Y) / span, origin.Y);
        }
        public PdfRect ToDisplay(double left, double bottom, double right, double top)
        {
            double x1 = A * left + B * bottom + C, y1 = D * left + E * bottom + F, x2 = A * right + B * top + C, y2 = D * right + E * top + F;
            double minX = Math.Clamp(Math.Min(x1, x2), 0, 1), minY = Math.Clamp(Math.Min(y1, y2), 0, 1), maxX = Math.Clamp(Math.Max(x1, x2), 0, 1), maxY = Math.Clamp(Math.Max(y1, y2), 0, 1);
            return new(minX, minY, maxX - minX, maxY - minY);
        }
    }
}

/// <summary>A page's characters with one box per character. Characters PDFium generates (spaces, line breaks) have empty boxes.</summary>
public sealed record PdfPageText(string Text, PdfRect[] Boxes)
{
    public static PdfPageText Empty { get; } = new("", []);

    /// <summary>Merges the boxes of a run of characters into one rectangle per line.</summary>
    public List<PdfRect> GetRects(int start, int length)
    {
        var rects = new List<PdfRect>();
        var end = Math.Min(Boxes.Length, start + length);
        PdfRect? run = null;
        for (var index = Math.Max(0, start); index < end; index++)
        {
            var box = Boxes[index];
            if (box.IsEmpty) continue;
            if (run is { } current)
            {
                var overlap = Math.Min(current.Bottom, box.Bottom) - Math.Max(current.Y, box.Y);
                var sameLine = overlap > Math.Min(current.Height, box.Height) * .5;
                // A box far outside the run's edges is a new column or a wrapped line, not the next character.
                var adjacent = box.X <= current.Right + current.Height * 2 && box.Right >= current.X - current.Height * 2;
                if (sameLine && adjacent)
                {
                    double x = Math.Min(current.X, box.X), y = Math.Min(current.Y, box.Y);
                    run = new(x, y, Math.Max(current.Right, box.Right) - x, Math.Max(current.Bottom, box.Bottom) - y);
                    continue;
                }
                rects.Add(current);
            }
            run = box;
        }
        if (run is { } last) rects.Add(last);
        return rects;
    }

    /// <summary>Returns the character under the point, or the nearest one; -1 when the page has no positioned text.</summary>
    public int CharacterAt(double x, double y)
    {
        var best = -1; var bestDistance = double.MaxValue;
        for (var index = 0; index < Boxes.Length; index++)
        {
            var box = Boxes[index];
            if (box.IsEmpty) continue;
            double dx = x < box.X ? box.X - x : x > box.Right ? x - box.Right : 0, dy = y < box.Y ? box.Y - y : y > box.Bottom ? y - box.Bottom : 0;
            if (dx == 0 && dy == 0) return index;
            // Favor the line the pointer is on: being beside a line beats being above or below a closer character.
            var distance = dx * dx + dy * dy * 16;
            if (distance < bestDistance) { bestDistance = distance; best = index; }
        }
        return best;
    }

    /// <summary>True when the point is on a character, allowing a little slack around it.</summary>
    public bool IsOverText(double x, double y)
    {
        foreach (var box in Boxes)
        {
            if (box.IsEmpty) continue;
            var slack = box.Height * .25;
            if (x >= box.X - slack && x <= box.Right + slack && y >= box.Y - slack && y <= box.Bottom + slack) return true;
        }
        return false;
    }
}

/// <summary>Finds text the way a reader expects: ignoring accents and line breaks, and optionally case.</summary>
public static class PdfSearch
{
    public static List<(int Start, int Length)> Find(string text, string query, bool caseSensitive)
    {
        var matches = new List<(int, int)>();
        var (needle, _) = Fold(query, caseSensitive);
        needle = needle.Trim();
        if (needle.Length == 0 || text.Length == 0) return matches;
        var (haystack, map) = Fold(text, caseSensitive);
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            var start = map[index];
            matches.Add((start, map[index + needle.Length - 1] - start + 1));
            if (index + needle.Length >= haystack.Length) break;
        }
        return matches;
    }

    // Returns the folded string and, for each folded character, its index in the original text.
    private static (string Text, int[] Map) Fold(string value, bool caseSensitive)
    {
        var builder = new StringBuilder(value.Length); var map = new int[value.Length];
        var space = false;
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (char.IsWhiteSpace(c))
            {
                // Runs of whitespace, including PDFium's line breaks, match a single typed space.
                if (space) continue;
                space = true; c = ' ';
            }
            else
            {
                space = false;
                if (c > 0x7F && !char.IsSurrogate(c) && c.ToString().Normalize(NormalizationForm.FormD) is { Length: > 0 } decomposed) c = decomposed[0];
                if (!caseSensitive) c = char.ToUpperInvariant(c);
            }
            map[builder.Length] = index; builder.Append(c);
        }
        return (builder.ToString(), map);
    }
}
