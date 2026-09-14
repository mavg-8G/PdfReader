namespace PdfReader.Core;

/// <summary>Holds a read-only lease so the bytes cannot change during range reads.</summary>
public sealed class DocumentSession : IDisposable
{
    public const long MaximumBytes = 512L * 1024 * 1024;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public long Length => Source.Length;
    public string Key => $"{Path.ToUpperInvariant()}|{Length}|{File.GetLastWriteTimeUtc(Path).Ticks}";
    internal FileStream Source { get; }
    private DocumentSession(string path, FileStream source) { Path = path; Source = source; }
    public static async Task<DocumentSession> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new DocumentException("Choose a PDF file", "Only files with a .pdf extension are supported.");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (stream.Length == 0) throw new DocumentException("This file is empty", "Choose a PDF that contains document data.");
            if (stream.Length > MaximumBytes) throw new DocumentException("This PDF is too large", "The safe opening limit is 512 MB. Split the document into smaller PDFs and try again.");
            var header = new byte[Math.Min(1024, stream.Length)];
            var read = await stream.ReadAsync(header, cancellationToken);
            if (!System.Text.Encoding.ASCII.GetString(header, 0, read).Contains("%PDF-", StringComparison.Ordinal))
                throw new DocumentException("This is not a valid PDF", "The file does not have a PDF header. It may be damaged or have the wrong extension.");
            stream.Position = 0;
            return new DocumentSession(path, stream);
        }
        catch { stream?.Dispose(); throw; }
    }
    public Stream CreateReadStream(ByteRange range) => new DocumentReadStream(Source, range);
    public async Task SaveCopyAsync(string destination, CancellationToken cancellationToken = default)
    {
        destination = System.IO.Path.GetFullPath(destination);
        if (string.Equals(destination, Path, StringComparison.OrdinalIgnoreCase))
            throw new DocumentException("Choose a different filename", "Save a copy preserves your original PDF. Choose another name or folder.");
        if (!string.Equals(System.IO.Path.GetExtension(destination), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new DocumentException("Use the .pdf extension", "Save the copy with a filename ending in .pdf.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var input = CreateReadStream(new ByteRange(0, Length - 1)))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => Source.Dispose();
}
public sealed class DocumentException(string title, string message) : Exception(message)
{
    public string Title { get; } = title;
}
internal sealed class DocumentReadStream(FileStream source, ByteRange range) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => range.Length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        count = (int)Math.Min(count, Length - _position);
        if (count <= 0) return 0;
        var read = RandomAccess.Read(source.SafeFileHandle, buffer.AsSpan(offset, count), range.Start + _position);
        _position += read;
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = (int)Math.Min(buffer.Length, Length - _position);
        if (count <= 0) return 0;
        var read = await RandomAccess.ReadAsync(source.SafeFileHandle, buffer[..count], range.Start + _position, cancellationToken);
        _position += read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => Length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (position < 0 || position > Length) throw new IOException("Seek is outside the requested PDF range.");
        return _position = position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
