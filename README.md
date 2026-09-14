# Folio — a local PDF reader for Windows

Folio is a C# / WinUI 3 desktop application with native commands, file pickers, search, thumbnails, dialogs, settings and window management. A bundled Mozilla PDF.js renderer runs inside the Windows WebView2 control to supply accurate page rendering, selectable text and document-wide search. No server or CDN is used by the installed application.

## Requirements

- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended.
- .NET SDK 10.0.400 or a newer 10.0.4xx patch, as pinned in `global.json`.
- Visual Studio with **WinUI application development** / Windows App SDK C# tools, or the corresponding Windows SDK and MSBuild tooling. The installed SDK must support targeting Windows 10 build 19041.
- Node.js 24 or later, used for initial PDF.js asset setup and tests only. No npm installation is required.
- Microsoft Edge WebView2 Evergreen Runtime. It is already installed on most Windows 11 systems; otherwise install it from [Microsoft's WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/).
- Development runs use the .NET 8 desktop runtime. Self-contained publish includes .NET and Windows App SDK runtime files.

WinUI is Windows-only. Folio adapts to narrow windows and Windows tablets, including touch controls; it does not run on Android or iOS.

## Install dependencies and build

Run these commands in PowerShell from the repository root:

```powershell
node --use-system-ca scripts/setup-viewer.mjs
node --use-system-ca scripts/setup-ocr.mjs
dotnet restore PdfReader/PdfReader.csproj --configfile NuGet.Config -p:Platform=x64
dotnet build PdfReader/PdfReader.csproj -p:Platform=x64 --no-restore
```

The setup script downloads PDF.js **6.3.289** from the npm registry, checks its pinned SHA-512 integrity, and copies the engine, worker, fonts, character maps, WASM codecs, viewer resources and license into `PdfReader/Viewer/vendor`. Assets are copied beside the executable during build and publish. Downloads happen at development/setup time only. Keep that complete directory with the application.

The only direct NuGet dependencies are **Microsoft.WindowsAppSDK 2.4.0** and **Microsoft.Windows.SDK.BuildTools 10.0.28000.2705**. WebView2 comes through Windows App SDK. `packages.lock.json` pins the NuGet dependency graph. PDF.js supplies PDF rendering. Tesseract.js supplies on-demand OCR through locally bundled browser assets; it adds no NuGet dependencies. No MVVM, printing, test-runner or UI packages are added.

Alternatively:

```powershell
./scripts/build.ps1
```

## Run and debug

Open `PdfReader.slnx`, make **PdfReader** the startup project, choose **Debug / x64**, and select **PdfReader (Unpackaged)** beside the Run button. Press F5.

```powershell
dotnet run --project PdfReader/PdfReader.csproj -p:Platform=x64 --launch-profile "PdfReader (Unpackaged)" --no-build --no-restore
```

You can pass a PDF path as the application's first argument:

```powershell
& ./PdfReader/bin/x64/Debug/net8.0-windows10.0.19041.0/PdfReader.exe 'C:\Documents\example.pdf'
```

**Old MSIX profile error:** This project uses `WindowsPackageType=None`. It must use the `Project` launch command, not `MsixPackage`. The checked-in launch settings contain only **PdfReader (Unpackaged)**. If Visual Studio still mentions **PdfReader (Package)**, reload the project or reopen the solution, then select the unpackaged profile. No package deployment or signing certificate is needed.

## Build a distributable folder

```powershell
./scripts/build.ps1 -Configuration Release -Platform x64 -Publish
# Or use -Platform ARM64 for a native Windows ARM64 build.
```

Distribute the **entire** `artifacts/Folio-x64` folder. Run `PdfReader.exe` from that folder. Self-contained output includes .NET and Windows App SDK, but still requires the installed WebView2 Runtime. This repository supplies an unpackaged application, not a signed installer or Store package. Sign the executable and create your organization's installer before a managed production rollout. ARM64 requires testing on ARM64 hardware.

## Read scanned pages with OCR

Open a PDF, go to the page you want, and choose **Read scanned page** on the toolbar (or in its overflow menu). Choose **English** or **Español**, then **Read page**. Select the recognized text and press **Ctrl+C**, or use **Find in recognized text** to find a word in that result. Rotate sideways pages upright before recognition. OCR can make mistakes, especially with handwriting, blurry photos, small print, or complex columns; check the result against the original.

OCR is deliberately on demand, one page and one language at a time. The engine is unloaded at startup and released after success, failure, cancellation, a page/rotation change, or document close. Starting print preparation also closes OCR. Only the current result is retained (at most 100,000 characters); recognized text and page images are not written to disk or IndexedDB. Changing pages clears the result. Save a copy preserves the original PDF bytes; OCR does not embed a text layer or populate document-wide search.

`scripts/setup-ocr.mjs` installs **Tesseract.js 7.0.0**, core **7.0.0**, and English/Spanish **1.0.0** quantized LSTM models from pinned, SHA-512-verified npm archives. The shipped OCR assets occupy approximately **16.1 MiB**. Only the worker, compatible LSTM CPU variants, two compressed language models, licenses and provenance files ship; npm packages, source maps, duplicate WASM binaries and the development download cache do not. Recognition uses local files exclusively and needs no internet connection.

OCR rendering is capped at **3 million pixels**, a maximum dimension of **3072**, and a scale of **2.5** (180 dpi for a normal PDF page). Its RGBA input canvas is at most about **11.5 MiB**. The canvas is released before recognition; encoded image bytes are transferred to one worker. This is an input budget, **not a cap on total app RAM**: PDF decoding, WebView2, language models and OCR working memory add to it. No whole-document OCR queue or accumulated image cache is created. A 90-second timeout releases a stalled OCR worker. Text extraction honors the PDF's copy permission.

## Features and behavior

- Open with the native picker, drop one PDF onto the window or document canvas, use recent history, or pass a path on the command line.
- Continuous scrolling; previous/next and direct page navigation; 25–500% zoom; fit width/page; clockwise rotation.
- A collapsible, virtualized native thumbnail list, with a 40-image cache. PDF.js renders visible pages and keeps a bounded page-view canvas cache; individual canvases are capped at eight million pixels. Page metadata and lightweight page placeholders are still retained.
- Search all available text, highlight matches, navigate matches, and match case. Select text and use Ctrl+C or the selection context menu. Copy and print restrictions in the PDF are respected.
- Native **Save a copy** preserves the original bytes and refuses the source path. In a desktop reader this is the equivalent of download/export. Source files remain read-only and cannot be edited while open.
- Print a validated range such as `1-5, 8`, then choose the printer, paper size, orientation and scaling in WebView2's Windows print preview. **Microsoft Print to PDF** can save the rendered output.
- Fullscreen and a single-page presentation mode with arrow/space navigation.
- Filename, page count, zoom, loading progress, cancellable opening/print preparation and recoverable errors.
- System, light and dark appearance; keyboard shortcuts; native accessible control names and live status messages; touch scrolling and zooming.
- A maximum of 20 recent documents with last page, scale and rotation, matched against path, size and last-modified time. Clear history from the welcome screen.
- Encrypted PDFs prompt for their password, including retry and cancellation. Passwords are never persisted.

## Practical limits

- Files must have a `.pdf` extension, a PDF header within the first 1,024 bytes, and a size from 1 byte to **512 MiB**. The renderer then validates the PDF structure. Documents above 100,000 pages are rejected. Large or pathological files may still exhaust renderer resources; process failure is reported with restart guidance. Opening has a 60-second timeout, suspended while entering a password.
- **Read scanned page** extracts English or Spanish text from the current page, including image-only PDFs. The result pane supports selection/copy and finding words on that page. Recognition is temporary: it does not create a searchable PDF or add matches to document-wide search.
- Existing annotations are displayed. **Creating/editing annotations, filling forms, editing PDF content, and validating digital signatures are not implemented.** Use a dedicated editor for those tasks, then reopen the result. There is no misleading annotation toolbar or save operation that silently discards edits.
- Printing rasterizes each chosen page at **144 dpi**, using high-quality JPEG images. It does not preserve vector text or searchable text in printed-to-PDF output. Jobs are bounded to 100 pages and 80 million total pixels (16 million per page). The app asks for a smaller batch if a job exceeds the memory limit. Select paper size and orientation in print preview; mixed paper sizes do not automatically configure printer trays. For vector-fidelity printing, save a copy and use a reader with native vector printing.
- Embedded PDF JavaScript, external navigation, attachments and dynamic XFA forms are not supported. Complex PDFs may render differently from their authoring tool. Page rotation affects the view and printed output, not the bytes saved by **Save a copy**.
- Narrator and touch behavior use standard WinUI/WebView2 accessibility. A tagged PDF provides a better reading order than an untagged PDF. Manual assistive-technology and printer-driver validation remain necessary before deployment.

## Tests

```powershell
./scripts/test.ps1
# Core + policy tests without launching a browser:
./scripts/test.ps1 -SkipBrowser
```

- `PdfReader.Tests`: dependency-free C# behavioral runner with a nonzero failure exit code. Covers opening, validation, missing/oversized files, cancellation, read-only leases, concurrent byte ranges, safe saving, navigation, zoom, print-range validation and settings recovery.
- `tests/policy.test.mjs`: Node's built-in test runner covers renderer input validation and user-facing error classification.
- `tests/ocr.test.mjs`: validates OCR image budgets, invalid dimensions/languages, cancellation during initialization, and worker failure cleanup.
- `tests/viewer.integration.mjs`: runs the **real bundled renderer** in an isolated headless Edge profile through Chromium's debugging protocol. Covers opening, page navigation, zoom/fit/rotation, text selection, searching and match movement, lazy thumbnails, presentation, print preparation and PDF output, cancellation, state restoration, password retries, permission restrictions, corruption and a 120-page document. It starts a test-only loopback HTTP server; the installed app does not use a server.
- `tests/fixtures.mjs` generates synthetic PDFs locally, including password and permission fixtures. Test password: `folio-test`. No private documents or network downloads are needed.
- OCR integration checks use locally generated image-only English and Spanish PDFs with the real bundled engine, including accented text, selecting search results, cancellation, worker release, copy restrictions, and narrow layouts.
- Set `FOLIO_TEST_BROWSER` to an installed Chromium browser executable if Edge is in a nonstandard location. Generated integration artifacts appear in `artifacts/tests`.

Do not equate headless print output with a physical printer test. Use `docs/acceptance.md` for the remaining Windows checks.

## Project structure

```text
PdfReader.slnx
PdfReader/
  App.xaml[.cs]                 Application startup and resources
  MainWindow.xaml[.cs]          Native shell and lifecycle
  MainWindow.Commands.cs       Commands, keyboard and adaptive layout
  MainWindow.Documents.cs      Document workflows and viewer events
  Views/WelcomeView.xaml[.cs]   Reusable welcome/recent-documents view
  ViewModels/ThumbnailItem.cs   Observable virtualized thumbnail model
  Services/ViewerHost.cs        Origin boundary and local range transport
  Viewer/                      Local PDF.js adapter, styles and policies
    vendor/                    Checksum-verified assets generated by setup
  Properties/                  Unpackaged launch/publish settings
PdfReader.Core/                 Platform-independent state, files and settings
PdfReader.Tests/                C# behavioral tests
tests/                         Renderer tests and synthetic fixture generator
scripts/                       Reproducible setup, build and test entry points
docs/                          Acceptance and privacy notes
```

## Privacy and engine references

Preferences and the browser profile are stored in `%LOCALAPPDATA%\Folio`. Preferences include recent file paths; clear recent history from the welcome screen on a shared device. Documents are held through read-only file handles. Only the active PDF token is exposed to the renderer, using range responses with `Cache-Control: no-store`. The renderer cannot navigate to external sites, start downloads or request device permissions. No analytics or document-upload endpoint is implemented. See [privacy notes](docs/privacy.md).

Architecture references: [Microsoft's WinUI WebView2 control](https://learn.microsoft.com/windows/apps/develop/ui/controls/webview2), [Mozilla PDF.js layers and distribution](https://mozilla.github.io/pdf.js/getting_started/), and [WebView2's file-drop bridge](https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2file).
