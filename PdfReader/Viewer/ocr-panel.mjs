import { OcrEngine, ocrSize } from './ocr-engine.mjs';

export function createOcrPanel({ getContext, focusDocument }) {
  const element = id => document.getElementById(id);
  const panel = element('ocrPanel'), status = element('ocrStatus'), text = element('ocrText');
  const read = element('ocrRead'), cancel = element('ocrCancel'), language = element('ocrLanguage');
  const progress = element('ocrProgress'), search = element('ocrSearch'), match = element('ocrMatch');
  let active = null, revision = 0, lastMatch = -1;
  function setBusy(busy) {
    read.disabled = language.disabled = busy;
    cancel.hidden = progress.hidden = !busy;
    panel.setAttribute('aria-busy', String(busy));
  }
  function clearText() { text.value = ''; search.value = ''; match.textContent = ''; lastMatch = -1; }
  function stop() {
    ++revision;
    if (active) {
      active.controller.abort(); active.render?.cancel(); active.engine?.dispose();
      clearTimeout(active.timeout);
      // Do not resize a canvas until PDF.js has acknowledged render cancellation.
      active = null;
    }
    setBusy(false);
  }
  function pageChanged() {
    stop(); clearText();
    const context = getContext();
    read.textContent = `Read page ${context.pageNumber ?? 1}`;
    status.textContent = context.canCopy ? 'Choose the page language, then read the page.' : 'Text extraction is restricted by this document’s permissions.';
    read.disabled = !context.canCopy;
  }
  function hide() {
    stop(); clearText(); panel.hidden = true;
    document.body.classList.remove('ocr-open');
  }
  function findNext() {
    const query = search.value.toLocaleLowerCase();
    const content = text.value.toLocaleLowerCase();
    if (!query) { match.textContent = ''; lastMatch = -1; return; }
    let next = content.indexOf(query, lastMatch + 1);
    if (next < 0) next = content.indexOf(query);
    lastMatch = next;
    match.textContent = next < 0 ? 'No match on this page' : 'Match selected in recognized text';
    if (next >= 0) { text.focus(); text.setSelectionRange(next, next + search.value.length); }
  }
  async function recognize() {
    if (active) return;
    const context = getContext();
    if (!context.pdf || !context.canCopy || context.printBusy) {
      status.textContent = context.printBusy ? 'Close print preview before reading a scanned page.' : 'Text extraction is unavailable for this document.';
      return;
    }
    stop(); clearText();
    const current = revision;
    const job = active = { controller: new AbortController(), render: null, engine: null, timeout: null };
    const valid = () => active === job && current === revision;
    const check = () => { if (!valid()) throw new DOMException('OCR cancelled.', 'AbortError'); };
    let canvas = null, page = null;
    setBusy(true); progress.removeAttribute('value');
    status.textContent = `Preparing page ${context.pageNumber}…`;
    job.timeout = setTimeout(() => {
      if (valid()) { stop(); status.textContent = 'Reading took too long. Try again with a clearer or simpler page.'; }
    }, 90_000);
    try {
      page = await context.pdf.getPage(context.pageNumber); check();
      const rotation = (page.rotate + context.rotation) % 360;
      const base = page.getViewport({ scale: 1, rotation });
      const size = ocrSize(base.width, base.height);
      canvas = document.createElement('canvas'); canvas.width = size.width; canvas.height = size.height;
      job.render = page.render({ canvasContext: canvas.getContext('2d'), viewport: page.getViewport({ scale: size.scale, rotation }), background: '#ffffff', annotationMode: 0 });
      await job.render.promise; job.render = null; check();
      const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
      canvas.width = canvas.height = 0; canvas = null; check();
      if (!blob) throw new Error('This page could not be prepared for OCR.');
      const bytes = new Uint8Array(await blob.arrayBuffer()); check();
      status.textContent = `Loading ${language.options[language.selectedIndex].text} recognition…`;
      job.engine = new OcrEngine({ signal: job.controller.signal, onProgress: data => {
        if (!valid()) return;
        if (data.status === 'recognizing text') {
          progress.value = Math.round(data.progress * 100);
          status.textContent = `Reading page ${context.pageNumber}… ${progress.value}%`;
        }
      } });
      const result = await job.engine.recognize(bytes, language.value); check();
      text.value = result.text;
      status.textContent = result.text ? `Page ${context.pageNumber} · ${result.truncated ? 'Text limited to 100,000 characters. ' : ''}Check the recognized text against the image.` : `No text found on page ${context.pageNumber}. Try another language or rotate the page upright.`;
      if (result.text) text.focus();
    } catch (error) {
      if (valid() && error.name !== 'AbortError' && error.name !== 'RenderingCancelledException') {
        status.textContent = 'Unable to read this page. ' + (error.message || 'Try again or repair the OCR files.');
      }
    } finally {
      if (canvas) canvas.width = canvas.height = 0;
      job.engine?.dispose(); clearTimeout(job.timeout);
      // A page that scrolled out of view should not stay decoded just for OCR.
      // PDF.js defers cleanup if another renderer is still using the page.
      if (page && context.pdf === getContext().pdf && context.pageNumber !== getContext().pageNumber) page.cleanup();
      if (valid()) { active = null; setBusy(false); }
    }
  }
  read.addEventListener('click', () => void recognize());
  cancel.addEventListener('click', () => { stop(); status.textContent = 'Reading cancelled. You can try again.'; });
  element('ocrClose').addEventListener('click', () => { hide(); focusDocument(); });
  element('ocrSelect').addEventListener('click', () => { text.focus(); text.select(); });
  search.addEventListener('input', () => { lastMatch = -1; match.textContent = ''; });
  search.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); findNext(); } });
  element('ocrFind').addEventListener('click', findNext);
  return {
    show() { if (panel.hidden) pageChanged(); panel.hidden = false; document.body.classList.add('ocr-open'); read.focus(); },
    hide, pageChanged,
    cancel() { stop(); status.textContent = 'Reading cancelled.'; }
  };
}
