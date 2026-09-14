import { OcrEngine, ocrSize } from './ocr-engine.mjs';

const copy = {
  en: {
    heading: 'Read scanned page',
    close: 'Close scanned-page reader',
    description: 'Extract text from a photo or scan, one page at a time. Everything stays on this device.',
    language: 'Language',
    english: 'English',
    spanish: 'Español',
    read: 'Read page {page}',
    cancel: 'Cancel',
    progress: 'Text recognition progress',
    recognized: 'Recognized text',
    placeholder: 'Text from this page will appear here.',
    select: 'Select all text',
    copyHint: 'Ctrl+C to copy',
    findLabel: 'Find in recognized text',
    findPlaceholder: 'Find on this page…',
    next: 'Next',
    note: 'OCR may make mistakes. This text is temporary; it isn’t added to the PDF or document-wide search.',
    choose: 'Choose the page language, then read the page.',
    restricted: 'Text extraction is restricted by this document’s permissions.',
    noMatch: 'No match on this page',
    match: 'Match selected in recognized text',
    closePrint: 'Close print preview before reading a scanned page.',
    unavailable: 'Text extraction is unavailable for this document.',
    preparing: 'Preparing page {page}…',
    timeout: 'Reading took too long. Try again with a clearer or simpler page.',
    loading: 'Loading {language} recognition…',
    reading: 'Reading page {page}… {progress}%',
    result: 'Page {page} · {limit}Check the recognized text against the image.',
    limit: 'Text limited to 100,000 characters. ',
    empty: 'No text found on page {page}. Try another language or rotate the page upright.',
    failed: 'Unable to read this page. Try another page or language. If the problem continues, repair or reinstall Folio.',
    cancelledRetry: 'Reading cancelled. You can try again.',
    cancelled: 'Reading cancelled.'
  },
  es: {
    heading: 'Leer página escaneada',
    close: 'Cerrar lector de páginas escaneadas',
    description: 'Extrae texto de una foto o escaneo, una página a la vez. Todo permanece en este dispositivo.',
    language: 'Idioma del documento',
    english: 'Inglés',
    spanish: 'Español',
    read: 'Leer página {page}',
    cancel: 'Cancelar',
    progress: 'Progreso del reconocimiento de texto',
    recognized: 'Texto reconocido',
    placeholder: 'El texto de esta página aparecerá aquí.',
    select: 'Seleccionar todo el texto',
    copyHint: 'Ctrl+C para copiar',
    findLabel: 'Buscar en el texto reconocido',
    findPlaceholder: 'Buscar en esta página…',
    next: 'Siguiente',
    note: 'El OCR puede cometer errores. Este texto es temporal; no se agrega al PDF ni a la búsqueda del documento.',
    choose: 'Elige el idioma de la página y luego léela.',
    restricted: 'Los permisos del documento restringen la extracción de texto.',
    noMatch: 'No hay coincidencias en esta página',
    match: 'Coincidencia seleccionada en el texto reconocido',
    closePrint: 'Cierra la vista previa de impresión antes de leer una página escaneada.',
    unavailable: 'La extracción de texto no está disponible para este documento.',
    preparing: 'Preparando la página {page}…',
    timeout: 'La lectura tardó demasiado. Inténtalo de nuevo con una página más clara o sencilla.',
    loading: 'Cargando reconocimiento en {language}…',
    reading: 'Leyendo la página {page}… {progress}%',
    result: 'Página {page} · {limit}Compara el texto reconocido con la imagen.',
    limit: 'Texto limitado a 100.000 caracteres. ',
    empty: 'No se encontró texto en la página {page}. Prueba otro idioma o gira la página a su posición correcta.',
    failed: 'No se pudo leer esta página. Prueba otra página o idioma. Si el problema continúa, repara o reinstala Folio.',
    cancelledRetry: 'Lectura cancelada. Puedes intentarlo de nuevo.',
    cancelled: 'Lectura cancelada.'
  }
};

export function createOcrPanel({ getContext, focusDocument }) {
  const element = id => document.getElementById(id);
  const panel = element('ocrPanel'), status = element('ocrStatus'), text = element('ocrText');
  const read = element('ocrRead'), cancel = element('ocrCancel'), language = element('ocrLanguage');
  const progress = element('ocrProgress'), search = element('ocrSearch'), match = element('ocrMatch');
  let active = null, revision = 0, lastMatch = -1, uiLanguage = 'en';
  const t = (key, fields = {}) => Object.entries(fields).reduce(
    (value, [name, replacement]) => value.replaceAll(`{${name}}`, replacement), copy[uiLanguage][key]);
  function setStaticText() {
    element('ocrHeading').textContent = t('heading');
    element('ocrClose').setAttribute('aria-label', t('close'));
    element('ocrDescription').textContent = t('description');
    element('ocrLanguageLabel').textContent = t('language');
    language.options[0].textContent = t('english');
    language.options[1].textContent = t('spanish');
    cancel.textContent = t('cancel');
    progress.setAttribute('aria-label', t('progress'));
    element('ocrTextLabel').textContent = t('recognized');
    text.placeholder = t('placeholder');
    element('ocrSelect').textContent = t('select');
    element('ocrCopyHint').textContent = t('copyHint');
    element('ocrSearchLabel').textContent = t('findLabel');
    search.placeholder = t('findPlaceholder');
    element('ocrFind').textContent = t('next');
    element('ocrNote').textContent = t('note');
  }
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
    read.textContent = t('read', { page: context.pageNumber ?? 1 });
    status.textContent = context.canCopy ? t('choose') : t('restricted');
    read.disabled = !context.canCopy;
  }
  function setLanguage(value) {
    uiLanguage = value === 'es' ? 'es' : 'en';
    setStaticText();
    if (!panel.hidden) pageChanged();
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
    match.textContent = next < 0 ? t('noMatch') : t('match');
    if (next >= 0) { text.focus(); text.setSelectionRange(next, next + search.value.length); }
  }
  async function recognize() {
    if (active) return;
    const context = getContext();
    if (!context.pdf || !context.canCopy || context.printBusy) {
      status.textContent = context.printBusy ? t('closePrint') : t('unavailable');
      return;
    }
    stop(); clearText();
    const current = revision;
    const job = active = { controller: new AbortController(), render: null, engine: null, timeout: null };
    const valid = () => active === job && current === revision;
    const check = () => { if (!valid()) throw new DOMException('OCR cancelled.', 'AbortError'); };
    let canvas = null, page = null;
    setBusy(true); progress.removeAttribute('value');
    status.textContent = t('preparing', { page: context.pageNumber });
    job.timeout = setTimeout(() => {
      if (valid()) { stop(); status.textContent = t('timeout'); }
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
      status.textContent = t('loading', { language: language.options[language.selectedIndex].text });
      job.engine = new OcrEngine({ signal: job.controller.signal, onProgress: data => {
        if (!valid()) return;
        if (data.status === 'recognizing text') {
          progress.value = Math.round(data.progress * 100);
          status.textContent = t('reading', { page: context.pageNumber, progress: progress.value });
        }
      } });
      const result = await job.engine.recognize(bytes, language.value); check();
      text.value = result.text;
      status.textContent = result.text
        ? t('result', { page: context.pageNumber, limit: result.truncated ? t('limit') : '' })
        : t('empty', { page: context.pageNumber });
      if (result.text) text.focus();
    } catch (error) {
      if (valid() && error.name !== 'AbortError' && error.name !== 'RenderingCancelledException') status.textContent = t('failed');
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
  cancel.addEventListener('click', () => { stop(); status.textContent = t('cancelledRetry'); });
  element('ocrClose').addEventListener('click', () => { hide(); focusDocument(); });
  element('ocrSelect').addEventListener('click', () => { text.focus(); text.select(); });
  search.addEventListener('input', () => { lastMatch = -1; match.textContent = ''; });
  search.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); findNext(); } });
  element('ocrFind').addEventListener('click', findNext);
  setStaticText();
  return {
    show() { if (panel.hidden) pageChanged(); panel.hidden = false; document.body.classList.add('ocr-open'); read.focus(); },
    hide, pageChanged, setLanguage,
    cancel() { stop(); status.textContent = t('cancelled'); }
  };
}
