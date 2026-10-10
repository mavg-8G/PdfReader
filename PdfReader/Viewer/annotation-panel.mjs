const MAX_ANNOTATIONS = 500, MAX_POINTS = 1024, MAX_RECTS = 100;

const strings = {
  en: {
    heading: 'Annotations', close: 'Close annotations', description: 'Notes, highlights, drawings, and form entries are saved in a separate .folio.json file beside the PDF.',
    note: 'Sticky note', draw: 'Draw', highlight: 'Highlight selection', noteText: 'New note text', notePlaceholder: 'Write a note, then click a page…',
    idle: 'Choose a tool. Select document text before highlighting it.', noteMode: 'Click a page to add your note.', drawMode: 'Draw on a page. Select Draw again to stop.', noSelection: 'Select some document text before highlighting it.', saved: 'Changes are saved beside the PDF.',
    delete: 'Delete note', open: 'Open note', closeNote: 'Close note'
  },
  es: {
    heading: 'Anotaciones', close: 'Cerrar anotaciones', description: 'Las notas, resaltados, dibujos y campos de formulario se guardan en un archivo .folio.json separado junto al PDF.',
    note: 'Nota adhesiva', draw: 'Dibujar', highlight: 'Resaltar selección', noteText: 'Texto de la nota', notePlaceholder: 'Escribe una nota y luego haz clic en una página…',
    idle: 'Elige una herramienta. Selecciona texto del documento antes de resaltarlo.', noteMode: 'Haz clic en una página para agregar la nota.', drawMode: 'Dibuja sobre una página. Selecciona Dibujar otra vez para detenerte.', noSelection: 'Selecciona texto del documento antes de resaltarlo.', saved: 'Los cambios se guardan junto al PDF.',
    delete: 'Eliminar nota', open: 'Abrir nota', closeNote: 'Cerrar nota'
  }
};

const clamp = value => Math.max(0, Math.min(1, value));
const id = () => globalThis.crypto?.randomUUID?.().replaceAll('{', '').replaceAll('}', '') ?? `folio-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
const element = name => document.getElementById(name);

export function createAnnotationPanel({ getContext, onChange, focusDocument }) {
  const panel = element('annotationPanel'), status = element('annotationStatus'), noteText = element('annotationNoteText');
  const note = element('annotationNote'), draw = element('annotationDraw'), highlight = element('annotationHighlight');
  let language = 'en', tool = 'none', markup = empty(''), drawing = null, selection = null, saveTimer = null;

  function t(key) { return strings[language][key]; }
  function empty(documentKey) { return { version: 1, documentKey, annotations: [], formValues: {} }; }
  function clean(value, documentKey = '') {
    const result = empty(typeof value?.documentKey === 'string' ? value.documentKey : documentKey);
    if (value?.version !== 1) return result;
    const known = new Set();
    for (const item of Array.isArray(value.annotations) ? value.annotations : []) {
      if (result.annotations.length >= MAX_ANNOTATIONS || !item || typeof item.id !== 'string' || known.has(item.id) || !Number.isInteger(item.page) || item.page < 1) continue;
      if (item.type === 'note' && point(item)) result.annotations.push({ id: item.id, type: 'note', page: item.page, x: item.x, y: item.y, text: String(item.text ?? '').slice(0, 5000) });
      if (item.type === 'highlight') {
        const rects = (Array.isArray(item.rects) ? item.rects : []).filter(rect).slice(0, MAX_RECTS);
        if (rects.length) result.annotations.push({ id: item.id, type: 'highlight', page: item.page, rects });
      }
      if (item.type === 'drawing') {
        const points = (Array.isArray(item.points) ? item.points : []).filter(point).slice(0, MAX_POINTS);
        if (points.length > 1) result.annotations.push({ id: item.id, type: 'drawing', page: item.page, points });
      }
      known.add(item.id);
    }
    for (const [key, field] of Object.entries(value?.formValues ?? {})) {
      if (Object.keys(result.formValues).length >= 2000 || !/^[A-Za-z0-9_.-]{1,160}$/.test(key) || ['__proto__', 'constructor', 'prototype'].includes(key) || !formValue(field)) continue;
      result.formValues[key] = Array.isArray(field) ? field.slice(0, 100).map(String) : field;
    }
    return result;
  }
  function point(value) { return Number.isFinite(value?.x) && Number.isFinite(value?.y) && value.x >= 0 && value.x <= 1 && value.y >= 0 && value.y <= 1; }
  function rect(value) { return point(value) && Number.isFinite(value.width) && Number.isFinite(value.height) && value.width > 0 && value.height > 0 && value.x + value.width <= 1 && value.y + value.height <= 1; }
  function formValue(value) { return value === null || typeof value === 'boolean' || (typeof value === 'string' && value.length <= 5000) || (Array.isArray(value) && value.length <= 100 && value.every(item => typeof item === 'string' && item.length <= 500)); }
  function clone() { return JSON.parse(JSON.stringify(markup)); }
  function schedule() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => { saveTimer = null; onChange(clone()); status.textContent = t('saved'); }, 450);
  }
  function updateCopy() {
    element('annotationHeading').textContent = t('heading'); element('annotationClose').setAttribute('aria-label', t('close'));
    element('annotationDescription').textContent = t('description'); note.textContent = t('note'); draw.textContent = t('draw'); highlight.textContent = t('highlight');
    element('annotationNoteLabel').textContent = t('noteText'); noteText.placeholder = t('notePlaceholder');
    if (tool === 'none') status.textContent = t('idle'); else status.textContent = tool === 'note' ? t('noteMode') : t('drawMode');
  }
  function setTool(next) {
    tool = tool === next ? 'none' : next;
    note.setAttribute('aria-pressed', String(tool === 'note')); draw.setAttribute('aria-pressed', String(tool === 'draw'));
    for (const layer of document.querySelectorAll('.folio-annotation-layer')) layer.classList.toggle('folio-canvas-active', tool === 'note' || tool === 'draw');
    updateCopy();
  }
  function canonicalPoint(value, rotation) {
    if (rotation === 90) return { x: clamp(value.y), y: clamp(1 - value.x) };
    if (rotation === 180) return { x: clamp(1 - value.x), y: clamp(1 - value.y) };
    if (rotation === 270) return { x: clamp(1 - value.y), y: clamp(value.x) };
    return { x: clamp(value.x), y: clamp(value.y) };
  }
  function displayedPoint(value, rotation) {
    if (rotation === 90) return { x: clamp(1 - value.y), y: clamp(value.x) };
    if (rotation === 180) return { x: clamp(1 - value.x), y: clamp(1 - value.y) };
    if (rotation === 270) return { x: clamp(value.y), y: clamp(1 - value.x) };
    return value;
  }
  function canonicalRect(value, rotation) {
    if (rotation === 90) return { x: value.y, y: 1 - value.x - value.width, width: value.height, height: value.width };
    if (rotation === 180) return { x: 1 - value.x - value.width, y: 1 - value.y - value.height, width: value.width, height: value.height };
    if (rotation === 270) return { x: 1 - value.y - value.height, y: value.x, width: value.height, height: value.width };
    return value;
  }
  function displayedRect(value, rotation) {
    if (rotation === 90) return { x: 1 - value.y - value.height, y: value.x, width: value.height, height: value.width };
    if (rotation === 180) return { x: 1 - value.x - value.width, y: 1 - value.y - value.height, width: value.width, height: value.height };
    if (rotation === 270) return { x: value.y, y: 1 - value.x - value.width, width: value.height, height: value.width };
    return value;
  }
  function pageNumber(page) { return Number.parseInt(page.dataset.pageNumber, 10); }
  function layerFor(page) {
    let layer = page.querySelector(':scope > .folio-annotation-layer');
    if (layer) return layer;
    layer = document.createElement('div'); layer.className = 'folio-annotation-layer'; page.append(layer);
    layer.addEventListener('pointerdown', startPointer); layer.addEventListener('pointermove', movePointer); layer.addEventListener('pointerup', endPointer); layer.addEventListener('pointercancel', endPointer);
    return layer;
  }
  function renderPage(page) {
    const number = pageNumber(page); if (!Number.isInteger(number)) return;
    const layer = layerFor(page); layer.replaceChildren(); layer.classList.toggle('folio-canvas-active', tool === 'note' || tool === 'draw');
    const rotation = getContext().rotation ?? 0;
    for (const annotation of markup.annotations.filter(item => item.page === number)) {
      if (annotation.type === 'highlight') for (const raw of annotation.rects) {
        const value = displayedRect(raw, rotation), mark = document.createElement('div'); mark.className = 'folio-highlight';
        mark.style.left = `${value.x * 100}%`; mark.style.top = `${value.y * 100}%`; mark.style.width = `${value.width * 100}%`; mark.style.height = `${value.height * 100}%`; layer.append(mark);
      }
      if (annotation.type === 'drawing') {
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); svg.classList.add('folio-drawing'); svg.setAttribute('viewBox', '0 0 1 1');
        const line = document.createElementNS('http://www.w3.org/2000/svg', 'polyline'); line.setAttribute('points', annotation.points.map(value => { const p = displayedPoint(value, rotation); return `${p.x},${p.y}`; }).join(' ')); svg.append(line); layer.append(svg);
      }
      if (annotation.type === 'note') renderNote(layer, annotation, rotation);
    }
  }
  function renderNote(layer, annotation, rotation) {
    const value = displayedPoint(annotation, rotation), shell = document.createElement('div'); shell.className = 'folio-note-shell';
    shell.style.left = `${value.x * 100}%`; shell.style.top = `${value.y * 100}%`;
    const button = document.createElement('button'); button.type = 'button'; button.className = 'folio-note'; button.textContent = '●'; button.title = t('open'); button.setAttribute('aria-label', t('open'));
    const card = document.createElement('div'); card.className = 'folio-note-card'; card.hidden = true;
    const input = document.createElement('textarea'); input.maxLength = 5000; input.value = annotation.text; input.setAttribute('aria-label', t('noteText'));
    const actions = document.createElement('div'); actions.className = 'folio-note-actions';
    const close = document.createElement('button'); close.type = 'button'; close.textContent = '×'; close.title = t('closeNote');
    const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = t('delete');
    button.addEventListener('click', () => { card.hidden = !card.hidden; if (!card.hidden) input.focus(); }); close.addEventListener('click', () => { card.hidden = true; });
    input.addEventListener('input', () => { annotation.text = input.value; schedule(); });
    remove.addEventListener('click', () => { markup.annotations = markup.annotations.filter(item => item.id !== annotation.id); renderAll(); schedule(); });
    actions.append(close, remove); card.append(input, actions); shell.append(button, card); layer.append(shell);
  }
  function renderAll() { for (const page of document.querySelectorAll('.page[data-page-number]')) renderPage(page); }
  function pointerPosition(event, page) { const bounds = page.getBoundingClientRect(); return { x: clamp((event.clientX - bounds.left) / bounds.width), y: clamp((event.clientY - bounds.top) / bounds.height) }; }
  function startPointer(event) {
    if (tool === 'none' || event.button !== 0 || !event.currentTarget.classList.contains('folio-canvas-active')) return;
    const page = event.currentTarget.parentElement, number = pageNumber(page), position = canonicalPoint(pointerPosition(event, page), getContext().rotation ?? 0);
    event.preventDefault(); event.currentTarget.setPointerCapture(event.pointerId);
    if (tool === 'note') {
      markup.annotations.push({ id: id(), type: 'note', page: number, ...position, text: noteText.value.trim().slice(0, 5000) || t('note') });
      setTool('none'); renderPage(page); schedule(); return;
    }
    drawing = { page, number, pointerId: event.pointerId, points: [position] };
  }
  function movePointer(event) {
    if (!drawing || drawing.pointerId !== event.pointerId) return;
    const point = canonicalPoint(pointerPosition(event, drawing.page), getContext().rotation ?? 0), previous = drawing.points.at(-1);
    if (drawing.points.length < MAX_POINTS && Math.hypot(point.x - previous.x, point.y - previous.y) > .0015) drawing.points.push(point);
  }
  function endPointer(event) {
    if (!drawing || drawing.pointerId !== event.pointerId) return;
    const finished = drawing; drawing = null;
    if (finished.points.length > 1) { markup.annotations.push({ id: id(), type: 'drawing', page: finished.number, points: finished.points }); renderPage(finished.page); schedule(); }
  }
  function rememberSelection() {
    const current = window.getSelection();
    if (!current?.rangeCount || current.isCollapsed) return;
    const range = current.getRangeAt(0);
    if (document.getElementById('viewerContainer').contains(range.commonAncestorContainer)) selection = range.cloneRange();
  }
  function highlightSelection() {
    const current = window.getSelection(); const range = current?.rangeCount && !current.isCollapsed ? current.getRangeAt(0) : selection;
    if (!range || range.collapsed) { status.textContent = t('noSelection'); return; }
    const rotation = getContext().rotation ?? 0, groups = new Map();
    for (const bounds of range.getClientRects()) {
      if (bounds.width < 1 || bounds.height < 1) continue;
      const page = [...document.querySelectorAll('.page[data-page-number]')].find(candidate => {
        const box = candidate.getBoundingClientRect(), x = bounds.left + bounds.width / 2, y = bounds.top + bounds.height / 2;
        return x >= box.left && x <= box.right && y >= box.top && y <= box.bottom;
      });
      if (!page) continue;
      const box = page.getBoundingClientRect(), displayed = { x: clamp((bounds.left - box.left) / box.width), y: clamp((bounds.top - box.top) / box.height), width: Math.min(1, bounds.width / box.width), height: Math.min(1, bounds.height / box.height) };
      if (!rect(displayed)) continue;
      const key = pageNumber(page), list = groups.get(key) ?? []; if (list.length < MAX_RECTS) list.push(canonicalRect(displayed, rotation)); groups.set(key, list);
    }
    for (const [page, rects] of groups) markup.annotations.push({ id: id(), type: 'highlight', page, rects });
    if (!groups.size) { status.textContent = t('noSelection'); return; }
    current?.removeAllRanges(); selection = null; renderAll(); schedule();
  }
  function captureForm(event) {
    const target = event.target;
    if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) || !target.closest('.annotationLayer')) return;
    const field = target.getAttribute('data-element-id'); if (!field) return;
    const value = target instanceof HTMLSelectElement && target.multiple ? [...target.selectedOptions].map(option => option.value) : target instanceof HTMLInputElement && (target.type === 'checkbox' || target.type === 'radio') ? target.checked : target.value;
    if (JSON.stringify(markup.formValues[field]) === JSON.stringify(value)) return;
    markup.formValues[field] = value; schedule();
  }
  document.addEventListener('selectionchange', rememberSelection);
  document.addEventListener('input', captureForm, true); document.addEventListener('change', captureForm, true);
  element('annotationClose').addEventListener('click', () => { hide(); focusDocument(); });
  note.addEventListener('click', () => setTool('note')); draw.addEventListener('click', () => setTool('draw')); highlight.addEventListener('click', highlightSelection);

  function show() { panel.hidden = false; document.body.classList.add('annotations-open'); updateCopy(); }
  function hide() { setTool('none'); panel.hidden = true; document.body.classList.remove('annotations-open'); }
  return {
    show, hide,
    setLanguage(value) { language = value === 'es' ? 'es' : 'en'; updateCopy(); renderAll(); },
    setMarkup(value, documentKey) { markup = clean(value, documentKey); renderAll(); },
    applyFormValues(pdf) { for (const [field, value] of Object.entries(markup.formValues)) pdf.annotationStorage.setValue(field, { value }); },
    renderPage(page) { if (page) renderPage(page); },
    reset() { clearTimeout(saveTimer); saveTimer = null; drawing = null; selection = null; markup = empty(''); hide(); },
    flush() { if (saveTimer) { clearTimeout(saveTimer); saveTimer = null; onChange(clone()); } }
  };
}
