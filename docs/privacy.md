# Local document boundary

The native host maps a single bundled `Viewer` directory to the synthetic `https://folio.local` origin. It handles the active random document URL separately; neither arbitrary paths nor the source directory are exposed to JavaScript. Requests to other network origins are rejected, and Content Security Policy disallows remote scripts, images, frames and connections.

PDF.js uses its worker for parsing and local WASM resources for codecs. Evaluation of PDF-provided code is disabled; no scripting manager is instantiated. Existing annotations render in read-only mode. External links, popups, automatic downloads and WebView permission requests are blocked. File drop uses Microsoft's `CoreWebView2File` bridge, followed by the same native validation used by the file picker.

The original is held read-only with a Windows sharing mode that prevents concurrent writes. Each range response has an independent position and uses the source handle without buffering the entire PDF. Save-copy streams to a temporary file beside the destination and replaces the destination only after the copy completes. The original path is rejected. The native save picker asks about replacing an existing destination.

The app stores recent paths, file metadata and view preferences under `%LOCALAPPDATA%\Folio\settings.json`. It never stores PDF passwords. WebView2 maintains its runtime profile in the same application directory. PDF responses specify `no-store`; this is not a secure-erasure guarantee for OS paging, crash dumps, browser runtime internals or Windows filesystem caches. Clear history removes the app's recent-document list, not the original documents.

There are no app analytics, remote search, cloud sync or document upload services. Windows, WebView2 updates and a printer selected by the user may have their own network behavior; a network printer receives the print job by the user's explicit print action.

Scanned-page OCR uses bundled Tesseract.js and local English/Spanish language data. Recognition runs only after the user requests it, with one page and one worker. OCR language caching in IndexedDB is disabled; page images and recognized text are not deliberately persisted. The worker is terminated after completion, cancellation, failure, timeout, or navigation, and the current result is cleared when its page or document changes. The normal Windows/browser paging and crash-dump caveats above also apply to OCR memory. Selecting and copying recognized text is a user action subject to the system clipboard's own history/sync settings. OCR respects the document's copy restriction.

Production maintenance includes keeping Windows, WebView2, Windows App SDK and the pinned PDF.js version patched, rerunning the included tests after engine updates, signing distributions, and validating representative PDFs and assistive technologies.
