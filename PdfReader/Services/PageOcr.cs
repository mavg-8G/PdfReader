using PdfReader.Core;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PdfReader.Services;

/// <param name="Text">The recognized text, one line per line.</param>
/// <param name="Layout">The same text with a box per character, in the page space the viewer uses.</param>
/// <param name="Language">The language that was actually used: "en", "es", or another installed language tag.</param>
/// <param name="Turned">Extra clockwise degrees the page had to be turned to read it; 0 when it was upright.</param>
public sealed record OcrPageResult(string Text, PdfPageText Layout, string Language, int Turned);

/// <summary>Reads text from one rendered page with the OCR engine built into Windows. Nothing leaves the device.</summary>
public static class PageOcr
{
    public const string Automatic = "auto";
    public const int MaximumCharacters = 100_000;
    private static readonly HashSet<string> English = ["the", "of", "and", "to", "in", "is", "that", "for", "it", "with", "as", "was", "on", "are", "this", "by", "be", "or", "from", "at", "have", "not"];
    private static readonly HashSet<string> Spanish = ["de", "la", "el", "que", "en", "los", "se", "del", "las", "por", "un", "con", "una", "es", "para", "su", "al", "lo", "como", "más", "mas", "no"];

    private static Windows.Globalization.Language? FindLanguage(string language) =>
        OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(candidate => candidate.LanguageTag.StartsWith(language, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Recognizes one page. <paramref name="language"/> is "en", "es" or <see cref="Automatic"/>, which picks between the
    /// installed languages from what the page says. A page that reads badly upright is also tried turned on its side.
    /// Returns null when Windows has no recognizer for the requested language.
    /// </summary>
    public static async Task<OcrPageResult?> RecognizeAsync(PdfDocument pdf, int page, int rotation, string language, string preferred, CancellationToken cancellationToken)
    {
        var engines = new List<(string Tag, OcrEngine Engine)>();
        foreach (var tag in language == Automatic ? new[] { preferred, preferred == "es" ? "en" : "es" } : [language])
            if (FindLanguage(tag) is { } found && OcrEngine.TryCreateFromLanguage(found) is { } engine) engines.Add((tag, engine));
        // With neither language installed, automatic mode still reads with whatever Windows offers for this user.
        if (engines.Count == 0 && language == Automatic && OcrEngine.TryCreateFromUserProfileLanguages() is { } fallback) engines.Add((fallback.RecognizerLanguage.LanguageTag, fallback));
        if (engines.Count == 0) return null;

        var (tagUsed, engineUsed) = engines[0];
        var image = await RenderAsync(pdf, page, rotation, cancellationToken);
        try
        {
            var result = await engineUsed.RecognizeAsync(image).AsTask(cancellationToken);
            // The first pass tells which language the page is in; the matching recognizer then reads accents correctly.
            if (engines.Count > 1 && GuessLanguage(result) is { } guess && guess != tagUsed)
            {
                var (otherTag, otherEngine) = engines[1];
                var second = await otherEngine.RecognizeAsync(image).AsTask(cancellationToken);
                if (Words(second) * 5 >= Words(result) * 4) { result = second; tagUsed = otherTag; engineUsed = otherEngine; }
            }
            int turned = 0, width = image.PixelWidth, height = image.PixelHeight;
            if (LooksUnreadable(result))
            {
                foreach (var extra in new[] { 90, 270, 180 })
                {
                    using var sideways = await RenderAsync(pdf, page, rotation + extra, cancellationToken);
                    var attempt = await engineUsed.RecognizeAsync(sideways).AsTask(cancellationToken);
                    if (Words(attempt) >= 3 && Words(attempt) > Words(result) * 3 / 2) { result = attempt; turned = extra; width = sideways.PixelWidth; height = sideways.PixelHeight; }
                }
            }
            return Build(result, width, height, (((rotation + turned) % 360) + 360) % 360, tagUsed, turned);
        }
        finally { image.Dispose(); }
    }

    private static async Task<SoftwareBitmap> RenderAsync(PdfDocument pdf, int page, int rotation, CancellationToken cancellationToken)
    {
        rotation = ((rotation % 360) + 360) % 360;
        var size = pdf.GetPageSize(page);
        double pageWidth = rotation is 90 or 270 ? size.Height : size.Width, pageHeight = rotation is 90 or 270 ? size.Width : size.Height;
        // 300 dpi suits printed text; very large pages are scaled down to what the engine accepts.
        var scale = Math.Min(300.0 / 72, Math.Min(OcrEngine.MaxImageDimension / pageWidth, OcrEngine.MaxImageDimension / pageHeight));
        scale = Math.Min(scale, Math.Sqrt(24_000_000 / (pageWidth * pageHeight)));
        int width = Math.Max(1, (int)(pageWidth * scale)), height = Math.Max(1, (int)(pageHeight * scale));
        var pixels = await Task.Run(() => { var buffer = new byte[width * height * 4]; pdf.Render(page, rotation, width, height, buffer); return buffer; }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    }

    private static IEnumerable<string> Tokens(OcrResult result) => result.Lines.SelectMany(line => line.Words).Select(word => word.Text.Trim('.', ',', ';', ':', '!', '?', '¿', '¡', '(', ')', '"', '\'').ToLowerInvariant());

    // Tokens that look like real words: only letters, at least three of them, with a vowel.
    private static int Words(OcrResult result) => Tokens(result).Count(token => token.Length >= 3 && token.All(char.IsLetter) && token.Any(c => "aeiouáéíóúü".Contains(c)));

    private static bool LooksUnreadable(OcrResult result)
    {
        int tokens = Tokens(result).Count(), words = Words(result);
        return tokens > 0 && (words < 3 || words * 2 < tokens);
    }

    private static string? GuessLanguage(OcrResult result)
    {
        int english = 0, spanish = 0;
        foreach (var token in Tokens(result))
        {
            if (English.Contains(token)) english++;
            if (Spanish.Contains(token)) spanish++;
            if (token.Any(c => "ñáéíóú".Contains(c))) spanish++;
        }
        return english == spanish ? null : english > spanish ? "en" : "es";
    }

    // Word boxes come back in pixels of the image as it was rendered; the viewer wants fractions of the unrotated page.
    private static OcrPageResult Build(OcrResult result, double imageWidth, double imageHeight, int rotation, string language, int turned)
    {
        var plain = new StringBuilder(); var characters = new StringBuilder(); var boxes = new List<PdfRect>();
        foreach (var line in result.Lines)
        {
            if (plain.Length + line.Text.Length > MaximumCharacters) break;
            if (plain.Length > 0) { plain.AppendLine(); characters.Append("\r\n"); boxes.Add(default); boxes.Add(default); }
            plain.Append(line.Text);
            for (var index = 0; index < line.Words.Count; index++)
            {
                var word = line.Words[index];
                if (index > 0) { characters.Append(' '); boxes.Add(default); }
                var bounds = word.BoundingRect;
                for (var letter = 0; letter < word.Text.Length; letter++)
                {
                    // The engine gives one box per word; its letters share it evenly, which is close enough to select by.
                    var shown = new PdfRect((bounds.X + bounds.Width * letter / word.Text.Length) / imageWidth, bounds.Y / imageHeight, bounds.Width / word.Text.Length / imageWidth, bounds.Height / imageHeight);
                    characters.Append(word.Text[letter]); boxes.Add(Clamp(ToPage(shown, rotation)));
                }
            }
        }
        return new(plain.ToString(), new PdfPageText(characters.ToString(), [.. boxes]), language, turned);
    }

    private static PdfRect ToPage(PdfRect shown, int rotation) => rotation switch
    {
        90 => new(shown.Y, 1 - shown.X - shown.Width, shown.Height, shown.Width),
        180 => new(1 - shown.X - shown.Width, 1 - shown.Y - shown.Height, shown.Width, shown.Height),
        270 => new(1 - shown.Y - shown.Height, shown.X, shown.Height, shown.Width),
        _ => shown
    };

    private static PdfRect Clamp(PdfRect rect)
    {
        double left = Math.Clamp(rect.X, 0, 1), top = Math.Clamp(rect.Y, 0, 1), right = Math.Clamp(rect.Right, 0, 1), bottom = Math.Clamp(rect.Bottom, 0, 1);
        return new(left, top, right - left, bottom - top);
    }
}
