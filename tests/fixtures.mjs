import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const padding = Buffer.from('28bf4e5e4e758a4164004e56fffa01082e2e00b6d0683e802f0ca9fe6453697a','hex');
const md5 = b => createHash('md5').update(b).digest();
const pad = p => Buffer.concat([Buffer.from(p, 'latin1'), padding]).subarray(0,32);
function rc4(key, data) {
  const s = Array.from({length:256},(_,i)=>i); let j=0;
  for(let i=0;i<256;i++){ j=(j+s[i]+key[i%key.length])%256; [s[i],s[j]]=[s[j],s[i]]; }
  const result=Buffer.alloc(data.length); let i=0; j=0;
  for(let n=0;n<data.length;n++){ i=(i+1)%256;j=(j+s[i])%256;[s[i],s[j]]=[s[j],s[i]];result[n]=data[n]^s[(s[i]+s[j])%256]; }
  return result;
}
export function makePdf(pages=3, { password, restricted=false }={}) {
  const objects = [], id=Buffer.from('folio-reader-test-document-id').subarray(0,16);
  let key, owner, user, permission=restricted ? -64 : -4;
  if(password){ owner=rc4(md5(pad('fixture-owner')).subarray(0,5),pad(password)); const p=Buffer.alloc(4);p.writeInt32LE(permission);key=md5(Buffer.concat([pad(password),owner,p,id])).subarray(0,5);user=rc4(key,padding); }
  objects.push('<< /Type /Catalog /Pages 2 0 R >>');
  objects.push(`<< /Type /Pages /Count ${pages} /Kids [${Array.from({length:pages},(_,i)=>`${4+i*2} 0 R`).join(' ')}] >>`);
  objects.push('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>');
  for(let i=0;i<pages;i++) {
    const pageObject=4+i*2, streamObject=pageObject+1;
    objects.push(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents ${streamObject} 0 R >>`);
    const line=t=>t.replace(/[()\\]/g,'\\$&');
    let stream=Buffer.from(`0.96 0.97 0.99 rg 0 0 612 792 re f\n0.26 0.34 0.7 rg 48 682 516 62 re f\nBT /F1 12 Tf 1 1 1 rg 66 709 Td (FOLIO / FIELD NOTES) Tj ET\nBT /F1 34 Tf 0.12 0.17 0.26 rg 48 612 Td (A little room to think.) Tj ET\nBT /F1 14 Tf 0.3 0.35 0.42 rg 48 570 Td (${line('Chapter '+(i+1)+' - The art of paying attention')}) Tj ET\nBT /F1 12 Tf 0.12 0.17 0.26 rg 48 506 Td (Focus makes space for the things that matter.) Tj 0 -26 Td (A document can be a beginning. Read slowly, stay curious,) Tj 0 -26 Td (and carry one useful idea into the rest of your day.) Tj 0 -52 Td (Search for Focus to explore the text in these pages.) Tj 0 -26 Td (Select this sentence and copy it into your notes.) Tj ET\n0.78 0.81 0.87 RG 48 114 m 564 114 l S\nBT /F1 10 Tf 0.4 0.45 0.5 rg 48 86 Td (Folio sample document - Page ${i+1} of ${pages}) Tj ET\n`,'latin1');
    if(key) { const suffix=Buffer.alloc(5); suffix.writeUIntLE(streamObject,0,3); stream=rc4(md5(Buffer.concat([key,suffix])).subarray(0,10),stream); }
    objects.push(Buffer.concat([Buffer.from(`<< /Length ${stream.length} >>\nstream\n`),stream,Buffer.from('\nendstream')]));
  }
  if(key) objects.push(`<< /Filter /Standard /V 1 /R 2 /Length 40 /O <${owner.toString('hex')}> /U <${user.toString('hex')}> /P ${permission} >>`);
  let bytes=Buffer.from('%PDF-1.7\n%\xe2\xe3\xcf\xd3\n','latin1'); const offsets=[0];
  objects.forEach((object,i)=>{offsets.push(bytes.length);bytes=Buffer.concat([bytes,Buffer.from(`${i+1} 0 obj\n`),Buffer.isBuffer(object)?object:Buffer.from(object),Buffer.from('\nendobj\n')]);});
  const xref=bytes.length;
  const trailer=`xref\n0 ${objects.length+1}\n0000000000 65535 f \n${offsets.slice(1).map(n=>String(n).padStart(10,'0')+' 00000 n \n').join('')}trailer\n<< /Size ${objects.length+1} /Root 1 0 R ${key?`/Encrypt ${objects.length} 0 R /ID [<${id.toString('hex')}> <${id.toString('hex')}>]`:''} >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.concat([bytes,Buffer.from(trailer)]);
}
export async function writeFixtures() {
  const dir=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'fixtures'); await mkdir(dir,{recursive:true});
  await Promise.all([
    writeFile(path.join(dir,'reading-notes.pdf'),makePdf()),
    writeFile(path.join(dir,'long-document.pdf'),makePdf(120)),
    writeFile(path.join(dir,'password.pdf'),makePdf(2,{password:'folio-test'})),
    writeFile(path.join(dir,'restricted.pdf'),makePdf(2,{password:'folio-test',restricted:true})),
    writeFile(path.join(dir,'corrupted.pdf'),'%PDF-1.7\nThis is deliberately not a PDF.'),
    writeFile(path.join(dir,'unsupported.txt'),'This is not a PDF.'),
    writeFile(path.join(dir,'empty.pdf'),'')
  ]);
  console.log('Generated local PDF test fixtures. Password: folio-test');
  return dir;
}
// Image-only fixture: no font resources or invisible text layer to accidentally OCR.
export function makeImagePdf(jpeg, width, height) {
  const content = Buffer.from('q 612 0 0 792 0 0 cm /Scan Do Q');
  const objects = [
    Buffer.from('<< /Type /Catalog /Pages 2 0 R >>'),
    Buffer.from('<< /Type /Pages /Count 1 /Kids [3 0 R] >>'),
    Buffer.from('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Scan 4 0 R >> >> /Contents 5 0 R >>'),
    Buffer.concat([Buffer.from(`<< /Type /XObject /Subtype /Image /Width ${width} /Height ${height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${jpeg.length} >>\nstream\n`), jpeg, Buffer.from('\nendstream')]),
    Buffer.concat([Buffer.from(`<< /Length ${content.length} >>\nstream\n`), content, Buffer.from('\nendstream')])
  ];
  let bytes = Buffer.from('%PDF-1.7\n'); const offsets = [0];
  for (let i=0; i<objects.length; i++) {
    offsets.push(bytes.length);
    bytes = Buffer.concat([bytes, Buffer.from(`${i+1} 0 obj\n`), objects[i], Buffer.from('\nendobj\n')]);
  }
  const xref = bytes.length;
  return Buffer.concat([bytes, Buffer.from(`xref\n0 6\n0000000000 65535 f \n${offsets.slice(1).map(n => String(n).padStart(10,'0')+' 00000 n \n').join('')}trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`)]);
}
if(process.argv[1] === fileURLToPath(import.meta.url)) await writeFixtures();
