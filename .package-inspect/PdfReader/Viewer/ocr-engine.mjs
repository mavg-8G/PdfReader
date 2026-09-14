export const OCR_MAX_PIXELS = 3_000_000;
export const OCR_MAX_DIMENSION = 3072;
export const OCR_MAX_TEXT = 100_000;

export function ocrSize(width, height) {
  if (![width, height].every(n => Number.isFinite(n) && n > 0)) throw new Error('This page has invalid dimensions.');
  const scale = Math.min(2.5, OCR_MAX_DIMENSION / width, OCR_MAX_DIMENSION / height,
    Math.sqrt(OCR_MAX_PIXELS / width / height));
  return { scale, width: Math.max(1, Math.floor(width * scale)), height: Math.max(1, Math.floor(height * scale)) };
}

// Adapter for the pinned Tesseract.js 7 worker protocol. Owning the Worker directly
// lets cancellation terminate it even during WASM/model initialization. Keep the
// protocol integration tests passing before updating the bundled engine.
export class OcrEngine {
  constructor({ signal, onProgress = () => {}, workerFactory = url => new Worker(url) } = {}) {
    this.pending = new Map();
    this.sequence = 0;
    this.worker = workerFactory(new URL('./ocr-vendor/worker.min.js', import.meta.url));
    this.worker.onmessage = ({ data: message }) => {
      const job = this.pending.get(message.jobId);
      if (!job) return;
      if (message.status === 'progress') { onProgress(message.data); return; }
      this.pending.delete(message.jobId);
      if (message.status === 'resolve') job.resolve(message.data);
      else job.reject(new Error(String(message.data || 'OCR failed.')));
    };
    this.worker.onerror = event => { event.preventDefault?.(); this.dispose(new Error('The OCR engine could not start. Repair the bundled OCR files and try again.')); };
    this.worker.onmessageerror = () => this.dispose(new Error('The OCR engine returned an unreadable result.'));
    this.signal = signal;
    this.abort = () => this.dispose(signal.reason ?? new DOMException('OCR cancelled.', 'AbortError'));
    signal?.addEventListener('abort', this.abort, { once: true });
    if (signal?.aborted) this.abort();
  }
  call(action, payload, transfer = []) {
    if (!this.worker) return Promise.reject(this.reason ?? new DOMException('OCR cancelled.', 'AbortError'));
    return new Promise((resolve, reject) => {
      const jobId = String(++this.sequence);
      this.pending.set(jobId, { resolve, reject });
      try { this.worker.postMessage({ workerId: 'folio-ocr', jobId, action, payload }, transfer); }
      catch (error) { this.pending.delete(jobId); reject(error); }
    });
  }
  async recognize(image, language) {
    if (!['eng', 'spa'].includes(language)) throw new Error('Choose English or Spanish.');
    const base = new URL('./ocr-vendor/', import.meta.url).href;
    await this.call('load', { options: { lstmOnly: true, corePath: base, logging: false } });
    await this.call('loadLanguage', { langs: language, options: { langPath: base, gzip: true, lstmOnly: true, cacheMethod: 'none' } });
    await this.call('initialize', { langs: language, oem: 1, config: { tessedit_pageseg_mode: '3' } });
    // Transfer encoded bytes rather than copying a large RGBA array between threads.
    const data = await this.call('recognize', { image, options: { rotateAuto: true }, output: { text: true } }, [image.buffer]);
    const text = String(data.text ?? '').trim();
    return { text: text.slice(0, OCR_MAX_TEXT), truncated: text.length > OCR_MAX_TEXT };
  }
  dispose(reason = new DOMException('OCR cancelled.', 'AbortError')) {
    this.reason = reason;
    this.worker?.terminate();
    this.worker = null;
    for (const job of this.pending.values()) job.reject(reason);
    this.pending.clear();
    this.signal?.removeEventListener('abort', this.abort);
  }
}
