import * as pdfjs from './vendor/build/pdf.mjs';
import { clampPage, normalizeScale, classifyError, validatePrintPages } from './policy.mjs';
import { createOcrPanel } from './ocr-panel.mjs';
const { PDFViewer, EventBus, PDFLinkService, PDFFindController, ScrollMode } = await import('./vendor/web/pdf_viewer.mjs');

pdfjs.GlobalWorkerOptions.workerSrc = new URL('./vendor/build/pdf.worker.mjs', import.meta.url).href;
const container = document.getElementById('viewerContainer');
const eventBus = new EventBus();
const linkService = new PDFLinkService({ eventBus });
linkService.externalLinkEnabled = false;
const findController = new PDFFindController({ eventBus, linkService });
const viewer = new PDFViewer({
  container, eventBus, linkService, findController,
  annotationMode: pdfjs.AnnotationMode.ENABLE,
  annotationEditorMode: pdfjs.AnnotationEditorType.DISABLE,
  enablePermissions: true, enableAutoLinking: false,
  maxCanvasPixels: 8_000_000, maxCanvasDim: 8192, enableDetailCanvas: false,
  imageResourcesPath: './vendor/web/images/'
});
linkService.setViewer(viewer);
let documentId = null, pdf = null, loadingTask = null, passwordCallback = null;
let generation = 0, openSequence = 0, printGeneration = 0, presentation = false, previousScale = 'page-width';
let restore = {}, thumbnailQueue = [], renderingThumbnail = false, thumbnailTask = null;
let printTask = null, printUrls = [], printBusy = false, permissions = null;
let readyForState = false, lastQuery = '', lastCaseSensitive = false;
const post = (type, fields = {}) => window.chrome?.webview?.postMessage({ type, id: documentId, ...fields });
const ocr = createOcrPanel({
  getContext: () => ({ pdf, pageNumber: viewer.currentPageNumber, rotation: viewer.pagesRotation,
    canCopy: !!pdf && (!permissions || permissions.has(pdfjs.PermissionFlag.COPY)), printBusy }),
  focusDocument: () => container.focus()
});

function reportState() {
  if (!pdf || !readyForState) return;
  post('state', { page: viewer.currentPageNumber, pages: pdf.numPages, zoom: viewer.currentScale,
    scale: String(viewer.currentScaleValue), rotation: viewer.pagesRotation });
  document.getElementById('announcement').textContent = `Page ${viewer.currentPageNumber} of ${pdf.numPages}`;
}
eventBus.on('pagesinit', () => {
  viewer.pagesRotation = [0,90,180,270].includes(restore.rotation) ? restore.rotation : 0;
  viewer.currentScaleValue = normalizeScale(restore.scale ?? 'page-width');
  viewer.currentPageNumber = clampPage(restore.page ?? 1, pdf.numPages);
  readyForState = true;
  reportState();
});
for (const event of ['pagechanging', 'scalechanging', 'rotationchanging']) eventBus.on(event, reportState);
for (const event of ['pagechanging', 'rotationchanging']) eventBus.on(event, () => ocr.pageChanged());
eventBus.on('pagerendered', ({ error }) => { if (error) post('warning', { message: 'A page could not be rendered completely. The PDF may contain damaged or unsupported content.' }); });
eventBus.on('updatefindmatchescount', ({ matchesCount }) => post('find', { ...matchesCount, pending: false }));
eventBus.on('updatefindcontrolstate', ({ state, matchesCount }) => post('find', { ...matchesCount, pending: state === 3, notFound: state === 1 }));

async function closeDocument() {
  ++generation; readyForState = false;
  ocr.hide();
  cancelPrint(); thumbnailQueue = []; thumbnailTask?.cancel(); passwordCallback = null;
  viewer.setDocument(null); linkService.setDocument(null); findController.setDocument(null);
  const task = loadingTask; loadingTask = null; pdf = null; permissions = null; documentId = null;
  if (task) await task.destroy();
}
async function openDocument(command) {
  const sequence = ++openSequence;
  await closeDocument();
  if (sequence !== openSequence) return;
  const current = generation;
  documentId = command.id;
  restore = command.restore ?? {};
  // The host exposes only this random document token. Paths never enter JavaScript.
  if (!/^[a-f0-9]{32}$/.test(documentId)) throw new Error('Invalid document token.');
  const url = new URL(command.url ?? `/document/${documentId}.pdf`, window.location.href);
  if (![window.location.origin, 'https://folio-document.invalid'].includes(url.origin) || url.pathname !== `/document/${documentId}.pdf`)
    throw new Error('Invalid document address.');
  const task = loadingTask = pdfjs.getDocument({
    url: url.href, rangeChunkSize: 262144,
    disableAutoFetch: true, disableStream: true, isEvalSupported: false,
    cMapUrl: './vendor/cmaps/', cMapPacked: true,
    standardFontDataUrl: './vendor/standard_fonts/', wasmUrl: './vendor/wasm/',
    iccUrl: './vendor/iccs/', enableXfa: false,
    maxImageSize: 32_000_000, canvasMaxAreaInBytes: 32_000_000
  });
  task.onProgress = ({ loaded, total }) => {
    if (current === generation) post('progress', { loaded, total });
  };
  task.onPassword = (callback, reason) => {
    if (current !== generation) return;
    passwordCallback = callback;
    post('password', { incorrect: reason === pdfjs.PasswordResponses.INCORRECT_PASSWORD });
  };
  try {
    const result = await task.promise;
    if (current !== generation) return;
    if (result.numPages > 100000) throw new Error('This document exceeds the 100,000-page safety limit.');
    pdf = result; permissions = await pdf.getPermissions();
    if (current !== generation) return;
    post('loaded', { pages: pdf.numPages, canPrint: canPrint() });
    linkService.setDocument(pdf);
    viewer.setDocument(pdf);
    if (pdf.isPureXfa) post('warning', { message: 'This PDF uses dynamic XFA forms, which are not supported. Open it in a reader with XFA support.' });
  } catch (error) {
    if (current !== generation) return;
    post('error', classifyError(error));
    await closeDocument();
  }
}

function canPrint() { return !permissions || permissions.has(pdfjs.PermissionFlag.PRINT); }
async function drainThumbnails() {
  if (renderingThumbnail) return;
  renderingThumbnail = true;
  try {
    while (thumbnailQueue.length && pdf) {
      const pageNumber = thumbnailQueue.shift(), current = generation, doc = pdf;
      try {
        const page = await doc.getPage(pageNumber);
        if (current !== generation) continue;
        const base = page.getViewport({ scale: 1 });
        const viewport = page.getViewport({ scale: Math.min(160 / base.width, 210 / base.height) });
        const canvas = document.createElement('canvas');
        canvas.width = Math.ceil(viewport.width); canvas.height = Math.ceil(viewport.height);
        thumbnailTask = page.render({ canvasContext: canvas.getContext('2d'), viewport, annotationMode: pdfjs.AnnotationMode.ENABLE });
        await thumbnailTask.promise;
        if (current === generation) post('thumbnail', { page: pageNumber, data: canvas.toDataURL('image/png').split(',')[1] });
        canvas.width = canvas.height = 0;
      } catch (error) { if (error.name !== 'RenderingCancelledException') post('thumbnailFailed', { page: pageNumber }); }
      finally { thumbnailTask = null; }
    }
  } finally { renderingThumbnail = false; }
}
function cancelPrint() {
  ++printGeneration; printTask?.cancel(); printTask = null; printBusy = false;
  document.getElementById('printContainer').replaceChildren();
  for (const url of printUrls) URL.revokeObjectURL(url);
  printUrls = [];
}
async function preparePrint(pages) {
  if (!pdf || printBusy) return;
  if (!canPrint()) throw new Error('Printing is restricted by this document’s permissions.');
  pages = validatePrintPages(pages, pdf.numPages);
  ocr.hide();
  cancelPrint(); printBusy = true;
  const current = printGeneration, doc = pdf;
  const printContainer = document.getElementById('printContainer');
  let pixels = 0;
  try {
    for (let index = 0; index < pages.length; index++) {
      const page = await doc.getPage(pages[index]);
      if (current !== printGeneration) return;
      const viewport = page.getViewport({ scale: 2, rotation: (page.rotate + viewer.pagesRotation) % 360 });
      pixels += Math.ceil(viewport.width) * Math.ceil(viewport.height);
      if (viewport.width * viewport.height > 16_000_000 || pixels > 80_000_000)
        throw new Error('This print batch exceeds the memory limit. Choose fewer pages and try again.');
      const canvas = document.createElement('canvas');
      canvas.width = Math.ceil(viewport.width); canvas.height = Math.ceil(viewport.height);
      printTask = page.render({ canvasContext: canvas.getContext('2d'), viewport, intent: 'print', background: '#ffffff', annotationMode: pdfjs.AnnotationMode.ENABLE });
      await printTask.promise;
      const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', .96));
      canvas.width = canvas.height = 0;
      if (current !== printGeneration) return;
      if (!blob) throw new Error('Unable to prepare this page for printing.');
      const url = URL.createObjectURL(blob); printUrls.push(url);
      const section = document.createElement('div'); section.className = 'printPage';
      const image = document.createElement('img'); image.src = url; image.alt = `Page ${pages[index]}`;
      section.append(image); printContainer.append(section); await image.decode();
      if (current !== printGeneration) return;
      post('printProgress', { current: index + 1, total: pages.length });
    }
    post('printReady', { pages: pages.length });
  } catch (error) {
    if (current === printGeneration) { cancelPrint(); post('printError', { message: error.message }); }
  }
}
window.addEventListener('afterprint', () => { cancelPrint(); post('printClosed'); });

function setPresentation(enabled) {
  if (!pdf || enabled === presentation) return;
  presentation = enabled;
  if (enabled) ocr.hide();
  document.body.classList.toggle('presentation', enabled);
  document.getElementById('exitPresentation').hidden = !enabled;
  if (enabled) { previousScale = viewer.currentScaleValue; viewer.scrollMode = ScrollMode.PAGE; viewer.currentScaleValue = 'page-fit'; }
  else { viewer.scrollMode = ScrollMode.VERTICAL; viewer.currentScaleValue = previousScale; }
  container.focus();
}
document.getElementById('exitPresentation').addEventListener('click', () => post('shortcut', { action: 'escape' }));

async function receive(command) {
  if (!command || typeof command.type !== 'string') return;
  if (command.type === 'theme') { document.documentElement.dataset.theme = command.value; return; }
  if (command.type === 'open') { await openDocument(command); return; }
  if (command.type === 'close') { ++openSequence; await closeDocument(); return; }
  if (command.id !== documentId) return;
  if (command.type === 'password') { const callback = passwordCallback; passwordCallback = null; callback?.(command.value); return; }
  if (!pdf) return;
  switch (command.type) {
    case 'ocr': ocr.show(); break;
    case 'closeOcr': ocr.hide(); break;
    case 'page': viewer.currentPageNumber = clampPage(command.value, pdf.numPages); break;
    case 'zoom': viewer.currentScaleValue = normalizeScale(command.value); break;
    case 'rotate': viewer.pagesRotation = (viewer.pagesRotation + 90) % 360; break;
    case 'find':
      lastQuery = String(command.query ?? '').slice(0, 500); lastCaseSensitive = !!command.caseSensitive;
      eventBus.dispatch('find', { source: window, type: command.again ? 'again' : '', query: lastQuery,
        caseSensitive: lastCaseSensitive, entireWord: false, highlightAll: true, findPrevious: !!command.previous, matchDiacritics: false }); break;
    case 'closeFind': eventBus.dispatch('findbarclose', { source: window }); break;
    case 'thumbnail':
      if (Number.isInteger(command.page) && command.page >= 1 && command.page <= pdf.numPages && !thumbnailQueue.includes(command.page)) {
        thumbnailQueue.push(command.page);
        if (thumbnailQueue.length > 40) post('thumbnailFailed', { page: thumbnailQueue.shift() });
        void drainThumbnails();
      } break;
    case 'print': await preparePrint(command.pages); break;
    case 'cancelPrint': cancelPrint(); post('printClosed'); break;
    case 'presentation': setPresentation(!!command.value); break;
    case 'focus': container.focus(); break;
  }
}
window.chrome?.webview?.addEventListener('message', event => {
  receive(event.data).catch(error => post('warning', { message: error.message || 'This action could not be completed.' }));
});
window.addEventListener('keydown', event => {
  const ctrl = event.ctrlKey || event.metaKey, key = event.key.toLowerCase();
  if (event.target.closest('#ocrPanel') && key === 'escape') { event.preventDefault(); ocr.hide(); container.focus(); return; }
  let action = null;
  if (ctrl) action = ({ o:'open', f:'find', p:'print', s:'save', w:'close', g:'page', '+':'zoomIn', '=':'zoomIn', '-':'zoomOut', '0':'fitPage', '1':'actualSize', '2':'fitWidth', r:'rotate' })[key];
  else action = ({ f11:'fullscreen', f5:'presentation', escape:'escape', f3: event.shiftKey ? 'findPrevious' : 'findNext' })[key];
  if (action) { event.preventDefault(); event.stopPropagation(); post('shortcut', { action }); return; }
  if (pdf && !event.target.closest('input,textarea,[contenteditable="true"]')) {
    let next = null;
    if (key === 'pagedown' || (presentation && ['arrowright','arrowdown',' '].includes(key))) next = viewer.currentPageNumber + 1;
    if (key === 'pageup' || (presentation && ['arrowleft','arrowup'].includes(key))) next = viewer.currentPageNumber - 1;
    if (key === 'home') next = 1;
    if (key === 'end') next = pdf.numPages;
    if (next !== null) { event.preventDefault(); viewer.currentPageNumber = clampPage(next, pdf.numPages); }
  }
});
container.addEventListener('wheel', event => {
  if (event.ctrlKey) { event.preventDefault(); if (pdf) viewer.currentScaleValue = normalizeScale(viewer.currentScale * (event.deltaY < 0 ? 1.1 : 1 / 1.1)); }
}, { passive: false });
// Never let a drop navigate WebView2 away from the trusted viewer.
window.addEventListener('dragover', event => { event.preventDefault(); event.dataTransfer.dropEffect = 'copy'; });
window.addEventListener('drop', event => {
  event.preventDefault();
  const files = event.dataTransfer.files;
  if (files.length === 1 && window.chrome?.webview?.postMessageWithAdditionalObjects)
    window.chrome.webview.postMessageWithAdditionalObjects({ type: 'drop' }, [files[0]]);
  else post('warning', { message: 'Drop one PDF file at a time, or use Open PDF.' });
});
window.addEventListener('contextmenu', event => { if (!window.getSelection()?.toString()) event.preventDefault(); });
post('ready');
