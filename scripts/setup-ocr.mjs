import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile, copyFile, readdir, stat, unlink } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const target = path.join(root, 'PdfReader/Viewer/ocr-vendor');
// Pin and verify every archive. Only browser runtime files and fast LSTM models ship.
const packages = [
  ['tesseract.js', '7.0.0', 'exPBkd+z+wM1BuMkx/Bjv43OeLBxhL5kKWsz/9JY+DXcXdiBjiAch0V49QR3oAJqCaL5qURE0vx9Eo+G5YE7mA=='],
  ['tesseract.js-core', '7.0.0', 'WnNH518NzmbSq9zgTPeoF8c+xmilS8rFIl1YKbk/ptuuc7p6cLNELNuPAzcmsYw450ca6bLa8j3t0VAtq435Vw=='],
  ['@tesseract.js-data/eng', '1.0.0', 'mbTumm6KQPUHyzTPQaF3ObXYnx0SqqfV2nabqFVQBwD6Kl7PhGSLSzOlfFTWy0P3BjghaSKA2W9GB19Jk+ZcTg=='],
  ['@tesseract.js-data/spa', '1.0.0', '9Ln+QKq/TNu4Hy4aOp5b4nXo9U0C6IqJMzNDpAJZe/fNtz6jXG9G/hQgR/Irxj+RGf0M7Xy1MNx1yl4wQUIfeg==']
];
await mkdir(target, { recursive: true });
for (const [name, version, integrity] of packages) {
  const shortName = name.split('/').at(-1);
  const staging = path.join(root, '.cache/ocr', `${shortName}-${version}`);
  await mkdir(staging, { recursive: true });
  const archive = path.join(staging, 'package.tgz');
  let bytes;
  try { bytes = await readFile(archive); } catch {
    const response = await fetch(`https://registry.npmjs.org/${name}/-/${shortName}-${version}.tgz`);
    if (!response.ok) throw new Error(`OCR download failed for ${name}: ${response.status}`);
    bytes = Buffer.from(await response.arrayBuffer());
  }
  if (createHash('sha512').update(bytes).digest('base64') !== integrity) throw new Error(`OCR integrity check failed: ${name}`);
  await writeFile(archive, bytes);
  execFileSync('tar', ['-xzf', archive, '-C', staging]);
  const source = path.join(staging, 'package');
  const copy = (from, to) => copyFile(path.join(source, from), path.join(target, to));
  if (name === 'tesseract.js') {
    await copy('dist/worker.min.js', 'worker.min.js');
    await copy('dist/worker.min.js.LICENSE.txt', 'worker.min.js.LICENSE.txt');
    await copy('LICENSE.md', 'LICENSE-tesseract.js');
  } else if (name === 'tesseract.js-core') {
    // legacyCore=false never selects the larger legacy recognition engines.
    // Keep all LSTM CPU variants so the worker can select compatible SIMD support.
    for (const file of await readdir(source)) {
      // These .wasm.js builds embed the WASM binary; shipping .wasm too duplicates it.
      if (/^tesseract-core.*lstm\.wasm\.js$/.test(file)) await copy(file, file);
    }
    await copy('LICENSE', 'LICENSE-tesseract.js-core');
  } else {
    await copy(`4.0.0_best_int/${shortName}.traineddata.gz`, `${shortName}.traineddata.gz`);
    await copy('package.json', `${shortName}-package.json`);
  }
}
await copyFile(path.join(target, 'LICENSE-tesseract.js-core'), path.join(target, 'LICENSE-tessdata'));
await writeFile(path.join(target, 'NOTICE.txt'), 'Tesseract.js and Tesseract.js-core 7.0.0: https://github.com/naptha/tesseract.js\nEnglish and Spanish trained data: https://github.com/naptha/tessdata (Apache-2.0). Distributed by @tesseract.js-data packages; package metadata included.\n');
// Remove only obsolete files from earlier OCR setup runs in this fixed output folder.
for (const file of await readdir(target)) {
  if (/^tesseract-core.*\.wasm$/.test(file) || file === 'tesseract.esm.min.js') await unlink(path.join(target, file));
}
const files = await readdir(target);
const size = (await Promise.all(files.map(file => stat(path.join(target, file))))).reduce((n, s) => n + s.size, 0);
await writeFile(path.join(target, '.version'), 'tesseract.js 7.0.0; core 7.0.0; eng/spa 1.0.0 best_int\n');
console.log(`Bundled offline OCR: ${(size / 1048576).toFixed(1)} MiB. SHA-512 verified; no runtime downloads.`);
