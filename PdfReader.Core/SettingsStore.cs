using System.Text.Json;
namespace PdfReader.Core;
public sealed record RecentDocument(string Path, string Key, string Name, int Page, int PageCount, string Scale, int Rotation, DateTimeOffset OpenedAt);
public sealed class ReaderSettings
{
    public string Theme { get; set; } = "System";
    public string Language { get; set; } = "en";
    public bool SidebarOpen { get; set; } = true;
    public List<RecentDocument> Recent { get; set; } = [];
}
public sealed class SettingsStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ReaderSettings Data { get; private set; } = new();
    public string? LastError { get; private set; }
    public void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Settings are too large.");
            Read(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { ResetAfterReadFailure(); }
    }
    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Settings are too large.");
            Read(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { ResetAfterReadFailure(); }
    }
    private void Read(string json)
    {
        Data = JsonSerializer.Deserialize<ReaderSettings>(json) ?? new();
        Data.Theme = Data.Theme is "Light" or "Dark" ? Data.Theme : "System";
        Data.Language = Data.Language == "es" ? "es" : "en";
        Data.Recent = (Data.Recent ?? []).Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Path)).Take(20).ToList();
    }
    private void ResetAfterReadFailure()
    {
        Data = new();
        LastError = "Reading preferences could not be restored. New preferences will be saved for this session.";
    }
    public RecentDocument? Find(string key) => Data.Recent.FirstOrDefault(r => r.Key == key);
    public void Remember(DocumentSession document, ReaderState state)
    {
        if (!state.IsOpen) return;
        Data.Recent.RemoveAll(r => string.Equals(r.Path, document.Path, StringComparison.OrdinalIgnoreCase));
        Data.Recent.Insert(0, new(document.Path, document.Key, document.Name, state.Page, state.PageCount, state.Scale, state.Rotation, DateTimeOffset.Now));
        Data.Recent = Data.Recent.Take(20).ToList();
    }
    public async Task<bool> SaveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LastError = "Reading preferences could not be saved. Check available space and folder permissions."; return false; }
        finally { _gate.Release(); }
    }
}
