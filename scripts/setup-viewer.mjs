import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile, cp } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const version = '6.3.289';
const integrity = 'ZHjSVpDa3D6izMq8/04lvkhkATUmL9px6ChPaXc1k6nU2Mrhlg1/7F0bdUqCwUjw3NsPTfPZsMDUU6ZIcRaeQw==';
const target = path.join(root, 'PdfReader/Viewer/vendor');
const staging = path.join(root, '.cache/pdfjs', version);
await mkdir(staging, { recursive: true });
const archive = path.join(staging, 'pdfjs.tgz');
let bytes;
try { bytes = await readFile(archive); } catch {
  const response = await fetch(`https://registry.npmjs.org/pdfjs-dist/-/pdfjs-dist-${version}.tgz`);
  if (!response.ok) throw new Error(`PDF.js download failed: ${response.status}`);
  bytes = Buffer.from(await response.arrayBuffer());
}
if (createHash('sha512').update(bytes).digest('base64') !== integrity) throw new Error('PDF.js integrity check failed.');
await writeFile(archive, bytes);
execFileSync('tar', ['-xzf', archive, '-C', staging]);
await mkdir(target, { recursive: true });
for (const name of ['build', 'web', 'cmaps', 'standard_fonts', 'wasm', 'iccs', 'LICENSE', 'package.json']) {
  await cp(path.join(staging, 'package', name), path.join(target, name), { recursive: true });
}
await writeFile(path.join(target, '.version'), version);
console.log(`Bundled PDF.js ${version}; SHA-512 verified. No runtime CDN is used.`);
