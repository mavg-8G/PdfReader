using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfReader.Core;

/// <summary>
/// Stores reader-owned markup for one PDF in the app's own data folder, so nothing is written next to the
/// document and removing the app's data removes every markup file. The PDF itself is always opened read-only.
/// </summary>
public sealed class DocumentMarkupStore(DocumentSession document, string folder)
{
    private const int MaximumFileBytes = 512 * 1024;
    private const int MaximumAnnotations = 500;
    private const int MaximumFields = 2_000;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>One file per PDF path. The name is a hash because a path is not a valid file name.</summary>
    public string Path { get; } = System.IO.Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.Path.ToUpperInvariant())))[..32].ToLowerInvariant() + ".json");

    /// <summary>Where earlier versions kept the markup: beside the PDF. Read once, then moved into <see cref="Path"/>.</summary>
    public string LegacyPath { get; } = document.Path + ".folio.json";

    public async Task<(DocumentMarkup Markup, string? Error)> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path)) return (await ImportLegacyAsync(cancellationToken), null);
        try { return (await ReadAsync(Path, cancellationToken), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return (DocumentMarkup.Empty(document.Key), "Annotations could not be loaded. The PDF was opened without them.");
        }
    }

    private async Task<DocumentMarkup> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("The annotation file is too large.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var markup = await JsonSerializer.DeserializeAsync<DocumentMarkup>(input, Json, cancellationToken);
        if (markup is null || markup.Version != 1 || !string.Equals(markup.DocumentKey, document.Key, StringComparison.Ordinal))
            throw new InvalidDataException("The annotation file belongs to a different version of this PDF.");
        return Normalize(markup, document.Key);
    }

    // A sidecar that cannot be read or belongs to an older version of the PDF is left where it is, untouched.
    private async Task<DocumentMarkup> ImportLegacyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(LegacyPath)) return DocumentMarkup.Empty(document.Key);
        try
        {
            var markup = await ReadAsync(LegacyPath, cancellationToken);
            // The sidecar is only removed once its contents are safely in the new location.
            if (await SaveAsync(markup, cancellationToken) is null && File.Exists(Path)) File.Delete(LegacyPath);
            return markup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return DocumentMarkup.Empty(document.Key);
        }
    }

    public async Task<string?> SaveAsync(JsonElement value, CancellationToken cancellationToken = default)
    {
        if (value.GetRawText().Length > MaximumFileBytes)
            return "Annotations could not be saved because they are too large.";
        DocumentMarkup? markup;
        try { markup = JsonSerializer.Deserialize<DocumentMarkup>(value.GetRawText(), Json); }
        catch (JsonException) { return "Annotations could not be saved because their data is invalid."; }
        if (markup is null || markup.Version != 1)
            return "Annotations could not be saved because their data is invalid.";
        return await SaveAsync(markup, cancellationToken);
    }

    public async Task<string?> SaveAsync(DocumentMarkup markup, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var clean = Normalize(markup, document.Key);
            // A document with nothing left on it keeps no file at all.
            if (clean.Annotations.Count == 0 && clean.FormValues.Count == 0) { File.Delete(Path); return null; }
            Directory.CreateDirectory(folder);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, clean, Json, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, Path, true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Annotations could not be saved. Check folder permissions and available space.";
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    private static DocumentMarkup Normalize(DocumentMarkup source, string key)
    {
        var annotations = new List<DocumentMarkupAnnotation>();
        foreach (var annotation in source.Annotations ?? [])
        {
            if (annotations.Count >= MaximumAnnotations || !IsId(annotation.Id) || annotation.Page is < 1 or > 100_000) continue;
            var type = annotation.Type?.ToLowerInvariant();
            if (type == "note" && IsPoint(annotation.X, annotation.Y))
            {
                annotations.Add(new DocumentMarkupAnnotation
                {
                    Id = annotation.Id, Type = type, Page = annotation.Page, X = annotation.X, Y = annotation.Y,
                    Text = (annotation.Text ?? "").Trim()[..Math.Min((annotation.Text ?? "").Trim().Length, 5_000)]
                });
            }
            else if (type == "highlight")
            {
                var rects = (annotation.Rects ?? []).Where(IsRect).Take(100).ToList();
                if (rects.Count > 0) annotations.Add(new DocumentMarkupAnnotation { Id = annotation.Id, Type = type, Page = annotation.Page, Rects = rects });
            }
            else if (type == "drawing")
            {
                var points = (annotation.Points ?? []).Where(p => IsPoint(p.X, p.Y)).Take(1_024).ToList();
                if (points.Count > 1) annotations.Add(new DocumentMarkupAnnotation { Id = annotation.Id, Type = type, Page = annotation.Page, Points = points });
            }
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, field) in source.FormValues ?? [])
        {
            if (fields.Count >= MaximumFields || !IsId(id) || !IsFormValue(field)) continue;
            fields[id] = field.Clone();
        }
        return new DocumentMarkup { Version = 1, DocumentKey = key, Annotations = annotations, FormValues = fields };
    }

    private static bool IsId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160 && value is not "__proto__" and not "constructor" and not "prototype" && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
    private static bool IsPoint(double x, double y) => double.IsFinite(x) && double.IsFinite(y) && x is >= 0 and <= 1 && y is >= 0 and <= 1;
    private static bool IsRect(DocumentMarkupRect rect) => IsPoint(rect.X, rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && rect.Width > 0 && rect.Height > 0 && rect.X + rect.Width <= 1 && rect.Y + rect.Height <= 1;
    private static bool IsFormValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { Length: <= 5_000 },
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
        JsonValueKind.Array => value.GetArrayLength() <= 100 && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: <= 500 }),
        _ => false
    };
}

public sealed class DocumentMarkup
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("documentKey")] public string DocumentKey { get; set; } = "";
    [JsonPropertyName("annotations")] public List<DocumentMarkupAnnotation> Annotations { get; set; } = [];
    [JsonPropertyName("formValues")] public Dictionary<string, JsonElement> FormValues { get; set; } = [];
    public static DocumentMarkup Empty(string key) => new() { DocumentKey = key };
}

public sealed class DocumentMarkupAnnotation
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("rects")] public List<DocumentMarkupRect> Rects { get; set; } = [];
    [JsonPropertyName("points")] public List<DocumentMarkupPoint> Points { get; set; } = [];
}

public sealed class DocumentMarkupRect
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
}

public sealed class DocumentMarkupPoint
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}
