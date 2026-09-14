import test from 'node:test';
import assert from 'node:assert/strict';
import { OcrEngine, ocrSize, OCR_MAX_PIXELS, OCR_MAX_DIMENSION } from '../PdfReader/Viewer/ocr-engine.mjs';

test('OCR raster budget bounds oversized and extreme-aspect pages', () => {
  for (const [width, height] of [[612,792], [100000,100000], [1,100000], [100000,1], [.001,.001]]) {
    const size = ocrSize(width, height);
    assert.ok(size.width * size.height <= OCR_MAX_PIXELS);
    assert.ok(Math.max(size.width, size.height) <= OCR_MAX_DIMENSION);
    assert.ok(size.width >= 1 && size.height >= 1);
  }
  for (const [width, height] of [[0,1], [1,Infinity], [-1,1], [NaN,1]]) assert.throws(() => ocrSize(width,height));
});

test('cancellation during engine startup terminates worker and rejects pending work', async () => {
  const controller = new AbortController();
  let terminated = 0, calls = 0;
  const worker = { postMessage() { calls++; }, terminate() { terminated++; } };
  const engine = new OcrEngine({ signal:controller.signal, workerFactory:() => worker });
  const promise = engine.recognize(new Uint8Array([1,2,3]), 'eng');
  const assertion = assert.rejects(promise, { name:'AbortError' });
  controller.abort(); await assertion;
  assert.equal(terminated, 1); assert.equal(calls, 1); assert.equal(engine.pending.size, 0);
  engine.dispose(); assert.equal(terminated, 1);
});

test('worker failures reject pending OCR work without leaving it alive', async () => {
  let terminated = false;
  const worker = { postMessage() {}, terminate() { terminated = true; } };
  const engine = new OcrEngine({ workerFactory:() => worker });
  const promise = engine.recognize(new Uint8Array([1]), 'spa');
  const assertion = assert.rejects(promise, /could not start/);
  worker.onerror({ preventDefault() {} }); await assertion;
  assert.ok(terminated); assert.equal(engine.pending.size, 0);
});

test('OCR rejects unsupported language paths before sending them to worker', async () => {
  let calls = 0;
  const engine = new OcrEngine({ workerFactory:() => ({ postMessage() { calls++; }, terminate() {} }) });
  await assert.rejects(engine.recognize(new Uint8Array([1]), '../../other'), /English or Spanish/);
  engine.dispose(); assert.equal(calls, 0);
});
