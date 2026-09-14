# Windows acceptance checks

Automated tests verify the engine and core services. Before distributing a release, validate the following on each supported Windows architecture and on the intended printer drivers:

- Launch the unpackaged profile from Visual Studio and launch the published executable on a machine without the .NET SDK. Check missing-WebView2 guidance.
- Open `tests/fixtures/reading-notes.pdf` with the native picker. Drop it onto both the welcome screen and the document canvas. Drop a folder, multiple files, a renamed text file and a corrupted PDF; check recovery.
- Navigate, select/copy text into Notepad, search `Focus`, move forward/back through six matches, test match case and a missing query. Check Ctrl+O/F/G/S/P/W, Ctrl+plus/minus, F3, F5, F11 and Escape.
- Open `password.pdf`, enter a wrong password, then `folio-test`; cancel a password prompt. Check that no password appears in settings. For `restricted.pdf`, printing must be disabled.
- Scroll `long-document.pdf`, jump to page 120 and back; watch that thumbnails and page images appear lazily. Inspect memory with representative large scanned and image-heavy documents.
- Use **Read scanned page** on the generated `scanned-english.pdf` and `scanned-spanish.pdf`; select the correct language, recognize, find a word, and copy the result into Notepad. Test a real photographed page and a rotated scan, blank-page feedback, cancellation during preparation/recognition, changing pages, closing and reopening the reader, and starting print preparation. Check that only one OCR worker runs and memory falls after it stops; record peak process-tree RAM on representative files. Copy-restricted documents must refuse OCR. Verify OCR works without internet access and that Save a copy does not embed OCR text.
- Save a copy, compare hashes, and verify the original file is unchanged. Try saving to the source filename, an unavailable drive and an unwritable folder.
- Print `1-2` and `1,3` to Microsoft Print to PDF. Inspect count, margins, rotation and legibility. Cancel preview, then print again. Test a physical printer, paper size/orientation, a mixed-size document, a memory-limit error, and cancellation during preparation.
- Close and reopen after changing page/zoom; quit and relaunch. Replace the PDF externally and verify old positions are not applied to changed content. Clear history.
- Test light, dark and system themes, high contrast, Narrator, keyboard-only focus traversal, reduced-motion settings, 100–200% display scaling, tablet touch, and a 420-DIP-wide window.
- Enter/exit presentation, navigate with arrows and Space, then return to continuous reading. Switch documents after cancelling an opening operation. Check all error banners remain recoverable.

Generated fixtures use standard Helvetica text and synthetic graphics. They are not a complete PDF conformance corpus. No physical print job is submitted by automated tests.
