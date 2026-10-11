using PdfReader.Core;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PdfReader.Services;

/// <summary>Reads text from one rendered page with the OCR engine built into Windows. Nothing leaves the device.</summary>
public static class PageOcr
{
    public const int MaximumCharacters = 100_000;

    /// <summary>True when Windows has a recognizer for this language ("en" or "es") installed.</summary>
    public static bool IsAvailable(string language) => FindLanguage(language) is not null;

    private static Windows.Globalization.Language? FindLanguage(string language) =>
        OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(candidate => candidate.LanguageTag.StartsWith(language, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the recognized text, or null when Windows has no recognizer for the language.</summary>
    public static async Task<string?> RecognizeAsync(PdfDocument pdf, int page, int rotation, string language, CancellationToken cancellationToken)
    {
        if (FindLanguage(language) is not { } recognizer || OcrEngine.TryCreateFromLanguage(recognizer) is not { } engine) return null;
        var size = pdf.GetPageSize(page);
        double pageWidth = rotation is 90 or 270 ? size.Height : size.Width, pageHeight = rotation is 90 or 270 ? size.Width : size.Height;
        // 300 dpi suits printed text; very large pages are scaled down to what the engine accepts.
        var scale = Math.Min(300.0 / 72, Math.Min(OcrEngine.MaxImageDimension / pageWidth, OcrEngine.MaxImageDimension / pageHeight));
        scale = Math.Min(scale, Math.Sqrt(24_000_000 / (pageWidth * pageHeight)));
        int width = Math.Max(1, (int)(pageWidth * scale)), height = Math.Max(1, (int)(pageHeight * scale));
        var pixels = await Task.Run(() => { var buffer = new byte[width * height * 4]; pdf.Render(page, rotation, width, height, buffer); return buffer; }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        var text = string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
        return text.Length > MaximumCharacters ? text[..MaximumCharacters] : text;
    }
}
