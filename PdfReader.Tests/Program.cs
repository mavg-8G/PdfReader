using PdfReader.Core;
using System.Text;
using System.Text.Json;

// Dependency-free behavioral test runner. Nonzero exit code on any failure.
var root = Path.Combine(Path.GetTempPath(), "FolioTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void Throws<T>(Action run) where T : Exception { try { run(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
async Task ThrowsAsync<T>(Func<Task> run) where T : Exception { try { await run(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
var valid = Path.Combine(root, "source.pdf");
await File.WriteAllTextAsync(valid, "%PDF-1.7\n0123456789\n%%EOF", Encoding.ASCII);

AsyncTest("Open validates a PDF header and keeps a read-only lease", async () =>
{
    using var doc = await DocumentSession.OpenAsync(valid);
    Equal("source.pdf", doc.Name);
    Equal(new FileInfo(valid).Length, doc.Length);
    if (OperatingSystem.IsWindows()) Throws<IOException>(() => File.Open(valid, FileMode.Open, FileAccess.Write).Dispose());
});
AsyncTest("Reject unsupported extension even when its bytes look like PDF", async () =>
{
    var path = Path.Combine(root, "wrong.txt"); File.Copy(valid, path);
    await ThrowsAsync<DocumentException>(() => DocumentSession.OpenAsync(path));
});
AsyncTest("Reject renamed non-PDF", async () =>
{
    var path = Path.Combine(root, "wrong.pdf"); await File.WriteAllTextAsync(path, "not a pdf");
    await ThrowsAsync<DocumentException>(() => DocumentSession.OpenAsync(path));
});
AsyncTest("Reject empty PDF", async () =>
{
    var path = Path.Combine(root, "empty.pdf"); await File.WriteAllBytesAsync(path, []);
    await ThrowsAsync<DocumentException>(() => DocumentSession.OpenAsync(path));
});
AsyncTest("Reject PDF above 512 MB before reading it", async () =>
{
    var path = Path.Combine(root, "oversize.pdf");
    using (var file = File.Create(path)) file.SetLength(DocumentSession.MaximumBytes + 1);
    await ThrowsAsync<DocumentException>(() => DocumentSession.OpenAsync(path));
});
AsyncTest("Missing file reports a file error", () => ThrowsAsync<FileNotFoundException>(() => DocumentSession.OpenAsync(Path.Combine(root, "missing.pdf"))));
AsyncTest("Cancelled open releases its lease", async () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => DocumentSession.OpenAsync(valid, cts.Token));
    using var file = File.Open(valid, FileMode.Open, FileAccess.Write);
});
AsyncTest("Concurrent range streams read independently", async () =>
{
    using var doc = await DocumentSession.OpenAsync(valid);
    using var first = doc.CreateReadStream(new(0, 4)); using var second = doc.CreateReadStream(new(9, 12));
    using var a = new StreamReader(first); using var b = new StreamReader(second);
    var results = await Task.WhenAll(a.ReadToEndAsync(), b.ReadToEndAsync());
    Equal("%PDF-", results[0]); Equal("0123", results[1]);
});
AsyncTest("Save copy preserves original bytes", async () =>
{
    using var doc = await DocumentSession.OpenAsync(valid);
    var destination = Path.Combine(root, "copy.pdf"); await doc.SaveCopyAsync(destination);
    Equal(Convert.ToHexString(await File.ReadAllBytesAsync(valid)), Convert.ToHexString(await File.ReadAllBytesAsync(destination)));
});
AsyncTest("Save copy refuses original path", async () => { using var doc = await DocumentSession.OpenAsync(valid); await ThrowsAsync<DocumentException>(() => doc.SaveCopyAsync(valid)); });
AsyncTest("Markup is saved in a sidecar without changing the PDF", async () =>
{
    var original = Convert.ToHexString(await File.ReadAllBytesAsync(valid));
    using var doc = await DocumentSession.OpenAsync(valid);
    var store = new DocumentMarkupStore(doc);
    using var payload = JsonDocument.Parse("""{"version":1,"annotations":[{"id":"note-1","type":"note","page":1,"x":0.2,"y":0.3,"text":"Remember this"},{"id":"highlight-1","type":"highlight","page":1,"rects":[{"x":0.1,"y":0.2,"width":0.3,"height":0.04}]},{"id":"drawing-1","type":"drawing","page":1,"points":[{"x":0.1,"y":0.1},{"x":0.2,"y":0.2}]}],"formValues":{"pdfjs_internal_id_1":"Ada","pdfjs_internal_id_2":true}}""");
    Equal<string?>(null, await store.SaveAsync(payload.RootElement));
    Equal(original, Convert.ToHexString(await File.ReadAllBytesAsync(valid)));
    var (markup, error) = await store.LoadAsync();
    Equal<string?>(null, error); Equal(3, markup.Annotations.Count); Equal("Ada", markup.FormValues["pdfjs_internal_id_1"].GetString());
    Equal(true, markup.FormValues["pdfjs_internal_id_2"].GetBoolean()); Equal(true, File.Exists(valid + ".folio.json"));
});
AsyncTest("Markup from an older PDF version is ignored", async () =>
{
    using var doc = await DocumentSession.OpenAsync(valid);
    var store = new DocumentMarkupStore(doc);
    await File.WriteAllTextAsync(store.Path, """{"version":1,"documentKey":"different","annotations":[],"formValues":{}}""");
    var (markup, error) = await store.LoadAsync();
    Equal(0, markup.Annotations.Count); Equal(true, error is not null);
    File.Delete(store.Path);
});
AsyncTest("Cancelled save leaves an existing destination intact", async () =>
{
    var destination = Path.Combine(root, "existing.pdf"); await File.WriteAllTextAsync(destination, "original destination");
    using var doc = await DocumentSession.OpenAsync(valid); using var cts = new CancellationTokenSource(); cts.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => doc.SaveCopyAsync(destination, cts.Token));
    Equal("original destination", await File.ReadAllTextAsync(destination));
    Equal(0, Directory.GetFiles(root, "*.tmp").Length);
});
foreach (var (header, start, end) in new (string?, long, long)[] { (null,0,99), ("bytes=10-19",10,19), ("bytes=90-",90,99), ("bytes=-10",90,99), ("bytes=90-999",90,99) })
    Test($"Read HTTP range {header ?? "whole file"}", () => { Equal(true, ByteRange.TryParse(header, 100, out var range)); Equal(new ByteRange(start,end), range); });
foreach (var header in new[] { "bytes=100-", "bytes=20-10", "bytes=-0", "bytes=0-1,4-5", "items=1-2", "bytes=garbage", "bytes=999999999999999999999-" })
    Test($"Reject malformed range {header}", () => Equal(false, ByteRange.TryParse(header,100,out _)));
Test("Navigation clamps to both boundaries", () => { var s = new ReaderState(); s.Open(12); Equal(1,s.Navigate(-5)); Equal(12,s.Navigate(100)); Equal(7,s.Navigate(7)); });
Test("Zoom limits survive invalid input", () => { var s = new ReaderState(); Equal(.25,s.SetZoom(.1)); Equal(5d,s.SetZoom(9)); Equal(1d,s.SetZoom(double.NaN)); });
Test("Fit modes and rotation can be restored", () => { var s = new ReaderState(); s.Open(10); s.Update(6,1.2,"page-width",450); Equal("page-width",s.Scale); Equal(90,s.Rotation); Equal(6,s.Page); });
Test("Close resets the document state", () => { var s = new ReaderState(); s.Open(4); s.Navigate(3); s.Reset(); Equal(false,s.IsOpen); Equal(1,s.Page); });
Test("Print ranges are validated, sorted and deduplicated", () => Equal("1,2,3,5", string.Join(',',PrintRange.Parse("5, 1-3, 2",10))));
foreach (var range in new[] { "", "0", "5-2", "101", "one", "1-101", "1-2-3" })
    Test($"Reject invalid print range '{range}'", () => Throws<FormatException>(() => PrintRange.Parse(range,100)));
AsyncTest("Settings restore page, scale, theme and history", async () =>
{
    var path = Path.Combine(root,"settings.json"); var store = new SettingsStore(path);
    using var doc = await DocumentSession.OpenAsync(valid); var state = new ReaderState(); state.Open(10); state.Update(7,1.4,"page-fit",90);
    store.Data.Theme="Dark"; store.Data.Language="es"; store.Remember(doc,state); Equal(true,await store.SaveAsync());
    var restored = new SettingsStore(path); restored.Load();
    Equal(7,restored.Find(doc.Key)!.Page); Equal("page-fit",restored.Find(doc.Key)!.Scale); Equal("Dark",restored.Data.Theme); Equal("es",restored.Data.Language);
});
AsyncTest("Damaged preferences recover without crashing", async () =>
{
    var path=Path.Combine(root,"damaged.json"); await File.WriteAllTextAsync(path,"{broken");
    var store=new SettingsStore(path); await store.LoadAsync(); Equal(0,store.Data.Recent.Count); Equal(true,store.LastError is not null);
});
AsyncTest("Unwritable preferences produce an actionable failure", async () =>
{
    var folder=Path.Combine(root,"blocked"); await File.WriteAllTextAsync(folder,"occupied");
    var store=new SettingsStore(Path.Combine(folder,"settings.json")); Equal(false,await store.SaveAsync()); Equal(true,store.LastError is not null);
});
// The PDFium tests read the checked-in fixtures; they are found by walking up from the test binary.
var fixtures = AppContext.BaseDirectory;
while (fixtures is not null && !Directory.Exists(Path.Combine(fixtures, "tests", "fixtures"))) fixtures = Path.GetDirectoryName(fixtures);
fixtures = fixtures is null ? throw new DirectoryNotFoundException("tests/fixtures was not found above the test binary.") : Path.Combine(fixtures, "tests", "fixtures");
AsyncTest("PDFium reads page count, size and permissions", async () =>
{
    using var session = await DocumentSession.OpenAsync(Path.Combine(fixtures, "reading-notes.pdf")); using var pdf = PdfDocument.Open(session);
    Equal(3, pdf.PageCount); Equal(new PdfSize(612, 792), pdf.GetPageSize(1)); Equal(true, pdf.CanPrint); Equal(true, pdf.CanCopy);
});
AsyncTest("PDFium renders a page into a BGRA buffer", async () =>
{
    using var session = await DocumentSession.OpenAsync(Path.Combine(fixtures, "reading-notes.pdf")); using var pdf = PdfDocument.Open(session);
    var pixels = new byte[153 * 198 * 4]; pdf.Render(1, 0, 153, 198, pixels);
    Equal(true, pixels.Chunk(4).All(pixel => pixel[3] == 255));
    // The fixture paints a dark blue banner near the top over a pale page.
    Equal(true, pixels.Chunk(4).Any(pixel => pixel[0] > 150 && pixel[2] < 100));
    var rotated = new byte[198 * 153 * 4]; pdf.Render(1, 90, 198, 153, rotated);
    Equal(false, pixels.AsSpan().SequenceEqual(rotated));
});
AsyncTest("PDFium text has one box per character in display fractions", async () =>
{
    using var session = await DocumentSession.OpenAsync(Path.Combine(fixtures, "reading-notes.pdf")); using var pdf = PdfDocument.Open(session);
    var text = pdf.GetText(2);
    Equal(text.Text.Length, text.Boxes.Length); Equal(true, text.Text.Contains("Chapter 2 - The art of paying attention"));
    var match = PdfSearch.Find(text.Text, "a little ROOM", false).Single();
    var rect = text.GetRects(match.Start, match.Length).Single();
    // "A little room to think." is set at 34 pt, 612 pt up a 792 pt page.
    Equal(true, rect.X is > .07 and < .09 && rect.Y is > .17 and < .23 && rect.Height is > .02 and < .06);
    Equal(match.Start, text.CharacterAt(rect.X + .001, rect.Y + rect.Height / 2));
    Equal(true, text.IsOverText(rect.X + .01, rect.Y + rect.Height / 2)); Equal(false, text.IsOverText(.99, .99));
});
AsyncTest("PDFium asks for a password and reports a wrong one", async () =>
{
    using var session = await DocumentSession.OpenAsync(Path.Combine(fixtures, "password.pdf"));
    try { PdfDocument.Open(session).Dispose(); throw new Exception("Expected a password request"); } catch (PdfPasswordException ex) { Equal(false, ex.Incorrect); }
    try { PdfDocument.Open(session, "wrong").Dispose(); throw new Exception("Expected a rejected password"); } catch (PdfPasswordException ex) { Equal(true, ex.Incorrect); }
    using var pdf = PdfDocument.Open(session, "folio-test");
    Equal(2, pdf.PageCount); Equal(true, pdf.GetText(1).Text.Contains("A little room to think."));
});
AsyncTest("PDFium honors print and copy restrictions", async () =>
{
    using var session = await DocumentSession.OpenAsync(Path.Combine(fixtures, "restricted.pdf")); using var pdf = PdfDocument.Open(session, "folio-test");
    Equal(false, pdf.CanPrint); Equal(false, pdf.CanCopy);
});
AsyncTest("PDFium rejects a damaged PDF", async () =>
{
    using var session = await DocumentSession.OpenAsync(Path.Combine(fixtures, "corrupted.pdf"));
    Throws<DocumentException>(() => PdfDocument.Open(session).Dispose());
});
AsyncTest("PDFium calls fail cleanly after the document is closed", async () =>
{
    using var session = await DocumentSession.OpenAsync(Path.Combine(fixtures, "reading-notes.pdf")); var pdf = PdfDocument.Open(session);
    pdf.Dispose(); pdf.Dispose();
    Throws<ObjectDisposedException>(() => pdf.GetText(1));
});
Test("Search ignores case, accents and line breaks", () =>
{
    const string text = "Página uno\r\ncontinúa aquí. PÁGINA";
    Equal("0:6,27:6", string.Join(',', PdfSearch.Find(text, "pagina", false).Select(m => $"{m.Start}:{m.Length}")));
    Equal("7:13", string.Join(',', PdfSearch.Find(text, "uno continua", false).Select(m => $"{m.Start}:{m.Length}")));
    Equal(1, PdfSearch.Find(text, "Pagina", true).Count); Equal(0, PdfSearch.Find(text, "   ", false).Count); Equal(0, PdfSearch.Find(text, "missing", false).Count);
});
Test("Text runs merge per line", () =>
{
    var text = new PdfPageText("ab\ncd", [new(.1, .1, .02, .02), new(.12, .1, .02, .02), default, new(.1, .2, .02, .02), new(.12, .2, .02, .02)]);
    var rects = text.GetRects(0, 5);
    Equal(2, rects.Count); Equal(true, Math.Abs(rects[0].Width - .04) < 1e-9 && Math.Abs(rects[1].Y - .2) < 1e-9);
    Equal(4, text.CharacterAt(.5, .5)); Equal(-1, PdfPageText.Empty.CharacterAt(.5, .5));
});
var failed=0;
foreach(var test in tests) { try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); } catch(Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); } }
Console.WriteLine($"{tests.Count-failed}/{tests.Count} core tests passed.");
// Delete only the uniquely created test directory under the temp root.
Directory.Delete(root,true);
return failed == 0 ? 0 : 1;

