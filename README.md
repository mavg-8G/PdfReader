# Folio — a local PDF reader for Windows

Folio is a C# / WinUI 3 desktop application with native commands, file pickers, search, thumbnails, dialogs, settings and window management. Pages are rendered in process by **PDFium**, the PDF engine used by Chromium, which also supplies selectable text and document-wide search. There is no browser component, server or CDN in the application.

## Requirements

- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended.
- .NET SDK 10.0.400 or a newer 10.0.4xx patch, as pinned in `global.json`.
- Visual Studio with **WinUI application development** / Windows App SDK C# tools, or the corresponding Windows SDK and MSBuild tooling. The installed SDK must support targeting Windows 10 build 19041.
- Development runs use the .NET 8 desktop runtime. Self-contained publish includes .NET and Windows App SDK runtime files.

WinUI is Windows-only. Folio adapts to narrow windows and Windows tablets, including touch controls; it does not run on Android or iOS.

## Install dependencies and build

Run these commands in PowerShell from the repository root:

```powershell
dotnet restore PdfReader/PdfReader.csproj --configfile NuGet.Config -p:Platform=x64
dotnet build PdfReader/PdfReader.csproj -p:Platform=x64 --no-restore
```

Restore downloads the native PDFium library from nuget.org; the build copies it beside the executable. There is no separate asset setup step.

The direct NuGet dependencies are **Microsoft.WindowsAppSDK 2.5.1**, **Microsoft.Windows.SDK.BuildTools 10.0.28000.2705**, and **bblanchon.PDFium.Win32 157.0.8086**, which contains only the prebuilt native `pdfium.dll` for x64 and ARM64. The C# bindings are hand-written in `PdfReader.Core/PdfiumNative.cs` and cover just the calls Folio uses. `packages.lock.json` pins the NuGet dependency graph. Scanned-page OCR and printing use APIs built into Windows. No MVVM, PDF wrapper, test-runner or UI packages are added.

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
& ./PdfReader/bin/x64/Debug/net10.0-windows10.0.26100.0/PdfReader.exe 'C:\Documents\example.pdf'
```

**Old MSIX profile error:** This project uses `WindowsPackageType=None`. It must use the `Project` launch command, not `MsixPackage`. The checked-in launch settings contain only **PdfReader (Unpackaged)**. If Visual Studio still mentions **PdfReader (Package)**, reload the project or reopen the solution, then select the unpackaged profile. No package deployment or signing certificate is needed.

## Build a distributable folder

```powershell
./scripts/build.ps1 -Configuration Release -Platform x64 -Publish
# Or use -Platform ARM64 for a native Windows ARM64 build.
```

Distribute the **entire** `artifacts/Folio-x64` folder. Run `PdfReader.exe` from that folder. Self-contained output includes .NET, Windows App SDK and PDFium; nothing else has to be installed. ARM64 requires testing on ARM64 hardware.

## Build a Windows MSI installer

```powershell
./scripts/build-installer.ps1
# For a later release:
./scripts/build-installer.ps1 -Version 1.0.1
```

The output is `artifacts/installer/Folio-1.0.0-x64.msi`, with installation instructions and a SHA-256 checksum beside it. Double-click the MSI, approve the administrator prompt, and follow the setup wizard. Launch **Folio** from the Start menu. Users do **not** need Visual Studio, the .NET SDK, or a separately installed .NET runtime. Setup installs into Program Files by default and supports repair, upgrades, downgrade prevention, and uninstall through Windows Settings. Rebuilding the same version replaces that version; use increasing three-part versions for releases.

The installer includes a self-contained Release build, the Windows App SDK, PDFium, and app icons. It has no prerequisites beyond Windows itself. After setup, PDF reading works offline. Uninstall removes installed program files and the Start-menu shortcut, while keeping PDFs and the user's `%LOCALAPPDATA%\Folio` reading preferences.

Building the installer requires the .NET SDK already used by this project, plus internet access for the initial tool and package restore. The script keeps pinned WiX **5.0.2** and its matching dialog extension in `.cache/wix`; Visual Studio is not required to build the MSI either. `-SkipPublish` packages an existing matching self-contained release from `artifacts/installer-publish/win-x64`. The MSI is currently **unsigned**, so Windows can identify its publisher as unknown; code signing requires a publisher-owned signing certificate. The MSI targets x64 Windows 10 build 19041 and later. No Store registration, file-association takeover, or installer runtime remains running with the app.

## Read scanned pages with OCR

Open a PDF, go to the page you want, and choose **Read scanned page** on the toolbar (or in its overflow menu), then **Read page**. The language is **Automatic** by default: Folio reads the page, works out whether it is English or Spanish, and reads it again with the matching recognizer when that differs, so accents come out right. Choose a language yourself to skip that. A page that reads badly as shown is also tried turned 90°, 270° and 180°, so a sideways scan does not have to be rotated first. Select the recognized text and press **Ctrl+C**, or use **Copy text**. OCR can make mistakes, especially with handwriting, blurry photos, small print, or complex columns; check the result against the original.

OCR is deliberately on demand, one page at a time. A recognized page gets a text layer for as long as the document stays open: its words can be selected and copied on the page, highlighted, and found with document search. That applies only to pages with no text of their own. The panel keeps the current result (at most 100,000 characters) until the page or rotation changes. Nothing recognized is written to disk, and **Save a copy** preserves the original PDF bytes.

Recognition uses the OCR engine built into Windows (`Windows.Media.Ocr`), so Folio ships no language models. A language is available when Windows has its text-recognition component installed; add languages in **Settings > Time & language > Language & region**. If the chosen language is missing, the panel says so instead of guessing with another one. Recognition runs locally and needs no internet connection.

The page is rendered for OCR at up to **300 dpi**, limited to the engine's maximum image dimension and **24 million pixels**. This is an input budget, **not a cap on total app RAM**. No whole-document OCR queue or accumulated image cache is created. Text extraction honors the PDF's copy permission.

## Features and behavior

- Open with the native picker, drop one PDF onto the window, use recent history, or pass a path on the command line.
- Continuous scrolling; previous/next and direct page navigation; 25–500% zoom; fit width/page; clockwise rotation.
- A collapsible, virtualized native thumbnail list, with a 40-image cache. Only the pages near the viewport exist as elements and bitmaps; each page bitmap is capped at twelve million pixels and re-rendered sharp after a zoom change. Page sizes are read once when the document opens.
- Search all available text, highlight matches, navigate matches, and match case; accents and line breaks are ignored when matching. Drag to select text, double-click to select a word, and copy with Ctrl+C or the context menu. Clicking a link to another page of the document goes there. Copy and print restrictions in the PDF are respected.
- Add sticky notes, text highlights and freehand drawings from **Annotate**. Folio keeps them in its own data folder, one file per document, and never writes next to the PDF or into it. Notes can be edited or deleted from their flyout; right-click a highlight or drawing to remove it. **Undo** and **Redo** (Ctrl+Z, Ctrl+Y) step through every annotation change made since the document was opened, up to 100 steps.
- Form fields are shown with the values stored in the PDF. Filling them in is not supported by the native viewer.
- Native **Save a copy** preserves the original bytes and refuses the source path. In a desktop reader this is the equivalent of download/export. Source files remain read-only and cannot be edited while open.
- Print a validated range such as `1-5, 8`, then choose the printer, paper size and orientation in the Windows print dialog. **Microsoft Print to PDF** can save the rendered output.
- Fullscreen and a single-page presentation mode with arrow/space navigation.
- Filename, page count, zoom, cancellable opening/print preparation and recoverable errors.
- System, light and dark appearance; keyboard shortcuts; native accessible control names and live status messages; touch scrolling. Zoom with the toolbar, Ctrl+plus/minus or Ctrl+mouse wheel.
- A maximum of 20 recent documents with last page, scale and rotation, matched against path, size and last-modified time. Clear history from the welcome screen.
- **Settings** shows the app version, taken from `Instalador/Package.appxmanifest`, and a link to the Windows page where Folio can be chosen as the default app for PDF files. Windows does not let an app make itself the default. The MSIX package built by the `Instalador` project declares the `.pdf` association, so an installed Folio appears under **Open with** and in **Settings > Apps > Default apps**, and opening a PDF from File Explorer starts Folio with that document. The MSI and the unpackaged folder do not register it.
- Encrypted PDFs prompt for their password, including retry and cancellation. Passwords are never persisted.

## Practical limits

- Files must have a `.pdf` extension, a PDF header within the first 1,024 bytes, and a size from 1 byte to **512 MiB**. PDFium then validates the PDF structure. Documents above 100,000 pages are rejected. PDFium runs inside the app process, so a file that crashes the engine closes Folio; large or pathological files may also exhaust memory.
- **Read scanned page** extracts English or Spanish text from the current page, including image-only PDFs, when Windows has that recognition language installed. Recognized pages stay selectable and searchable only until the document is closed; Folio does not create a searchable PDF. Word positions come from the recognizer, so selection on a scan is approximate.
- Existing PDF annotations and form values are displayed. Folio's own sticky notes, highlights and drawings are stored in `%LOCALAPPDATA%\Folio\Annotations`, in a file named after a hash of the PDF's path. They are tied to that path and to the PDF's size and modification time: moving, renaming or changing the PDF leaves them behind. A `<document>.pdf.folio.json` file written beside a PDF by an earlier version is moved into that folder the first time the PDF is opened; one that no longer matches its PDF is left alone. A document with no annotations left keeps no file. Filling forms, editing PDF-native annotations, editing PDF content, and validating digital signatures are not implemented.
- Printing rasterizes each chosen page at **144 dpi**. It does not preserve vector text or searchable text in printed-to-PDF output. Jobs are bounded to 100 pages and 80 million total pixels (16 million per page). The app asks for a smaller batch if a job exceeds the memory limit. Each PDF page is fitted to one sheet of the paper selected in the print dialog; mixed paper sizes do not automatically configure printer trays. For vector-fidelity printing, save a copy and use a reader with native vector printing.
- Embedded PDF JavaScript, links to web addresses or other files, attachments and XFA forms are not supported. Complex PDFs may render differently from their authoring tool. Page rotation affects the view and printed output, not the bytes saved by **Save a copy**.
- Toolbar, panels and dialogs use standard WinUI accessibility. The page area is an image with a text overlay for selection and search; it does not expose the document text to Narrator. Pinch-to-zoom is not implemented. Manual assistive-technology and printer-driver validation remain necessary before deployment.

## Tests

```powershell
./scripts/test.ps1
```

- `PdfReader.Tests`: dependency-free C# behavioral runner with a nonzero failure exit code. Covers opening, validation, missing/oversized files, cancellation, read-only leases, concurrent byte ranges, safe saving, annotation storage (location, migration of old sidecars, cleanup of empty files), navigation, zoom, print-range validation and settings recovery. It also drives the real PDFium engine over the checked-in fixtures: page count and size, rendering and rotation, per-character text boxes, search folding, password prompts and retries, permission restrictions, corruption, and use after close.
- `tests/fixtures/` holds synthetic PDFs, including password and permission fixtures. Test password: `folio-test`. `tests/fixtures.mjs` regenerates them with Node.js if they ever need to change; running the tests does not need Node. No private documents or network downloads are needed.

The viewer control itself (scrolling, pointer selection, annotations, printing, OCR) has no automated tests. Use `docs/acceptance.md` for those Windows checks.

## Project structure

The application artwork lives in `PdfReader/Assets`. `Folio.ico` supplies the executable and window icons (16–256 px); the PNG assets keep their Windows manifest filenames and sizes. `Folio.svg` is the editable vector master. To regenerate the matching assets from the shared vector paths, run `./scripts/generate-icons.ps1` in PowerShell 7 on Windows. It uses built-in .NET drawing APIs and writes a light/dark preview to `artifacts/icons/folio-icons-preview.png`. No image library is added to the app.

```text
PdfReader.slnx
PdfReader/
  App.xaml[.cs]                 Application startup and resources
  MainWindow.xaml[.cs]          Native shell and lifecycle
  MainWindow.Commands.cs       Commands, keyboard and adaptive layout
  MainWindow.Documents.cs      Document workflows, thumbnails and printing
  MainWindow.Panels.cs         Scanned-page and annotation side panels
  Controls/PdfViewer.xaml[.cs]  Native page view: layout, virtualization, rendering
  Controls/PdfViewer.Interaction.cs  Selection, search, annotations and input
  Views/WelcomeView.xaml[.cs]   Reusable welcome/recent-documents view
  ViewModels/ThumbnailItem.cs   Observable virtualized thumbnail model
  Services/PageOcr.cs           Windows OCR over one rendered page
  Services/PrintService.cs      Windows print dialog bridge
  Properties/                  Unpackaged launch/publish settings
PdfReader.Core/                 State, files, settings, markup and the PDFium bindings
PdfReader.Tests/                C# behavioral tests
tests/                         Synthetic PDF fixtures and their generator
scripts/                       Build, installer and test entry points
docs/                          Acceptance and privacy notes
```

## Privacy and engine references

Preferences and annotations are stored in `%LOCALAPPDATA%\Folio`. When Folio is installed from its MSIX package, Windows keeps that folder inside the package's own data and removes it with the app; the MSI and the unpackaged folder leave it in place. Preferences include recent file paths; clear recent history from the welcome screen on a shared device. Documents are held through read-only file handles and read by PDFium in process. Nothing in the app opens web addresses or starts downloads. No analytics or document-upload endpoint is implemented. See [privacy notes](docs/privacy.md).

Engine references: [PDFium](https://pdfium.googlesource.com/pdfium/), the [prebuilt PDFium binaries](https://github.com/bblanchon/pdfium-binaries) Folio ships, and [Windows.Media.Ocr](https://learn.microsoft.com/uwp/api/windows.media.ocr).
