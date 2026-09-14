import assert from 'node:assert/strict';
import http from 'node:http';
import {spawn} from 'node:child_process';
import {readFile,writeFile,mkdir,stat} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {writeFixtures,makeImagePdf} from './fixtures.mjs';

// Tests the actual bundled renderer in an installed Chromium browser. No npm test dependencies.
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const fixtures=await writeFixtures();
const artifact=path.join(root,'artifacts/tests'); await mkdir(artifact,{recursive:true});
const token='0123456789abcdef0123456789abcdef'; let active='reading-notes.pdf';
const types={'.js':'text/javascript','.mjs':'text/javascript','.css':'text/css','.html':'text/html','.pdf':'application/pdf','.wasm':'application/wasm','.svg':'image/svg+xml','.png':'image/png','.bcmap':'application/octet-stream','.ttf':'font/ttf'};
const server=http.createServer(async(req,res)=>{
  try{
    const url=new URL(req.url,'http://localhost');
    let file=url.pathname.startsWith('/document/')?path.join(fixtures,active):path.join(root,'PdfReader/Viewer',decodeURIComponent(url.pathname));
    if(!file.startsWith(path.join(root,'PdfReader/Viewer'))&&!file.startsWith(fixtures)){res.writeHead(403).end();return;}
    const bytes=await readFile(file),range=req.headers.range?.match(/^bytes=(\d+)-(\d*)$/);
    res.setHeader('Content-Type',types[path.extname(file)]??'application/octet-stream');res.setHeader('Accept-Ranges','bytes');res.setHeader('Cache-Control','no-store');
    if(range){const start=Number(range[1]),end=Math.min(bytes.length-1,range[2]?Number(range[2]):bytes.length-1);res.writeHead(206,{'Content-Range':`bytes ${start}-${end}/${bytes.length}`,'Content-Length':end-start+1});res.end(bytes.subarray(start,end+1));}
    else {res.setHeader('Content-Length',bytes.length);res.end(bytes);}
  }catch{res.writeHead(404).end();}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const origin=`http://127.0.0.1:${server.address().port}`;
const browserPath=process.env.FOLIO_TEST_BROWSER??'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const browser=spawn(browserPath,['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-port=0',`--user-data-dir=${path.join(root,'.cache','browser-tests-'+Date.now())}`,'about:blank'],{stdio:['ignore','ignore','pipe'],windowsHide:true});
let socket,failed=false;
try{
  const debuggerUrl=await new Promise((resolve,reject)=>{let output='';const timeout=setTimeout(()=>reject(new Error('Chromium did not start. Set FOLIO_TEST_BROWSER to an installed browser executable.')),20000);browser.stderr.on('data',chunk=>{output+=chunk;const match=output.match(/DevTools listening on (ws:\/\/[^\s]+)/);if(match){clearTimeout(timeout);resolve(match[1]);}});browser.on('error',reject);});
  socket=new WebSocket(debuggerUrl);await new Promise((resolve,reject)=>{socket.addEventListener('open',resolve,{once:true});socket.addEventListener('error',reject,{once:true});});
  let sequence=0;const pending=new Map();
  socket.addEventListener('message',event=>{const m=JSON.parse(event.data);if(m.id&&pending.has(m.id)){const p=pending.get(m.id);pending.delete(m.id);clearTimeout(p.timeout);m.error?p.reject(new Error(m.error.message)):p.resolve(m.result);}});
  function cdp(method,params={},sessionId){return new Promise((resolve,reject)=>{const id=++sequence,timeout=setTimeout(()=>{pending.delete(id);reject(new Error(`CDP timeout: ${method}`));},30000);pending.set(id,{resolve,reject,timeout});socket.send(JSON.stringify({id,method,params,sessionId}));});}
  const target=await cdp('Target.createTarget',{url:'about:blank'});
  const {sessionId}=await cdp('Target.attachToTarget',{targetId:target.targetId,flatten:true});
  const send=(method,params={})=>cdp(method,params,sessionId);
  async function evaluate(expression){const r=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(r.exceptionDetails)throw new Error(r.exceptionDetails.text+': '+r.exceptionDetails.exception?.description);return r.result.value;}
  await send('Page.enable');await send('Runtime.enable');
  await send('Page.addScriptToEvaluateOnNewDocument',{source:`window.__ocrWorkers=new Set();const NativeWorker=window.Worker;window.Worker=class extends NativeWorker {constructor(url,options){super(url,options);if(String(url).includes('ocr-vendor/worker.min.js')){__ocrWorkers.add(this);window.__ocrPeak=Math.max(window.__ocrPeak||0,__ocrWorkers.size);}}terminate(){__ocrWorkers.delete(this);return super.terminate();}};`});
  await send('Page.addScriptToEvaluateOnNewDocument',{source:`window.__messages=[];window.__listeners=[];window.__errors=[];window.addEventListener('error',e=>__errors.push(e.message));window.addEventListener('unhandledrejection',e=>__errors.push(String(e.reason)));window.chrome??={};window.chrome.webview={postMessage:m=>__messages.push(m),addEventListener:(name,fn)=>__listeners.push(fn)};window.__command=m=>__listeners.forEach(fn=>fn({data:m}));`});
  await send('Emulation.setDeviceMetricsOverride',{width:1000,height:760,deviceScaleFactor:1,mobile:false});
  await send('Page.navigate',{url:origin+'/index.html'});
  async function wait(expression,label=expression,timeout=15000){const deadline=Date.now()+timeout;while(Date.now()<deadline){if(await evaluate(expression))return;await new Promise(r=>setTimeout(r,50));}throw new Error(`Timed out: ${label}\n${JSON.stringify(await evaluate('({messages:__messages,errors:__errors,ocrStatus:document.getElementById("ocrStatus")?.textContent,ocrText:document.getElementById("ocrText")?.value,ocrWorkers:__ocrWorkers.size})'))}`);}
  async function command(type,fields={}){await evaluate(`__command(${JSON.stringify({type,id:token,...fields})})`);}
  async function open(name,restore){active=name;await evaluate('__messages=[]');await command('open',{restore});}
  const pass=name=>console.log('PASS '+name);
  await wait('__messages.some(m=>m.type==="ready")','viewer bootstrap');
  await open('reading-notes.pdf');await wait('__messages.some(m=>m.type==="state")');
  assert.equal(await evaluate('__messages.find(m=>m.type==="loaded").pages'),3);pass('open a real PDF and report page count');
  await wait('document.querySelector(".textLayer span")?.textContent?.length>0','text rendering');
  await command('page',{value:2});await wait('__messages.at(-1)?.type==="state"&&__messages.at(-1).page===2');pass('navigate to a specific page');
  await command('zoom',{value:1.5});await wait('__messages.filter(m=>m.type==="state").at(-1)?.zoom===1.5');pass('zoom to 150 percent');
  await command('zoom',{value:'page-fit'});await wait('__messages.filter(m=>m.type==="state").at(-1)?.scale==="page-fit"');pass('fit page');
  await command('zoom',{value:'page-width'});await wait('__messages.filter(m=>m.type==="state").at(-1)?.scale==="page-width"');pass('fit width');
  await command('rotate');await wait('__messages.filter(m=>m.type==="state").at(-1)?.rotation===90');pass('rotate pages');
  await command('find',{query:'Focus',caseSensitive:false});await wait('__messages.some(m=>m.type==="find"&&m.total===6)');pass('search across all pages');
  await command('find',{query:'Focus',again:true});await wait('__messages.filter(m=>m.type==="find").at(-1)?.current>1');pass('advance to next search result');
  await command('find',{query:'nothing-matches-this'});await wait('__messages.filter(m=>m.type==="find").at(-1)?.notFound===true');pass('no-match search state');
  const selected=await evaluate(`(()=>{const span=[...document.querySelectorAll('.textLayer span')].find(s=>s.textContent.includes('Focus'));const range=document.createRange();range.selectNodeContents(span);const selection=getSelection();selection.removeAllRanges();selection.addRange(range);return selection.toString();})()`);
  assert.ok(selected.includes('Focus'));pass('selectable text layer');
  // Rasterize independent text into JPEG, then wrap it in an image-only PDF.
  const scan = await evaluate(`(()=>{const c=document.createElement('canvas');c.width=1224;c.height=1584;const x=c.getContext('2d');x.fillStyle='white';x.fillRect(0,0,c.width,c.height);x.fillStyle='black';x.font='40px Arial';x.fillText('Scanned pages can be read offline.',90,180);x.fillText('Folio keeps your documents private.',90,260);x.fillText('The quick brown fox jumps over the lazy dog.',90,340);return c.toDataURL('image/jpeg',.95).split(',')[1];})()`);
  await writeFile(path.join(fixtures,'scanned-english.pdf'),makeImagePdf(Buffer.from(scan,'base64'),1224,1584));
  await open('scanned-english.pdf');await wait('__messages.some(m=>m.type==="state")');
  await wait('document.querySelector(".page canvas")');
  assert.equal(await evaluate('[...document.querySelectorAll(".textLayer span")].some(s=>s.textContent.trim())'),false);
  assert.equal(await evaluate('__ocrWorkers.size'),0);pass('scanned PDF does not load OCR until requested');
  await command('ocr');await evaluate('document.getElementById("ocrRead").click()');
  await wait('__ocrWorkers.size===1','OCR worker startup');
  await evaluate('document.getElementById("ocrCancel").click()');
  assert.equal(await evaluate('__ocrWorkers.size'),0);pass('cancel OCR during startup releases its worker');
  await evaluate('document.getElementById("ocrRead").click()');
  await wait('document.getElementById("ocrText").value.includes("Scanned pages can be read offline")','real OCR of scanned English text',60000);
  await wait('__ocrWorkers.size===0','OCR worker released after success');
  assert.equal(await evaluate('__ocrPeak'),1);pass('English OCR runs with one worker and releases it after completion');
  await evaluate('document.getElementById("ocrSearch").value="private";document.getElementById("ocrFind").click()');
  assert.equal(await evaluate('(()=>{const t=document.getElementById("ocrText");return t.value.slice(t.selectionStart,t.selectionEnd);})()'),'private');
  pass('find recognized text and select it for copying');
  await send('Emulation.setDeviceMetricsOverride',{width:420,height:760,deviceScaleFactor:1,mobile:false});
  await new Promise(r=>setTimeout(r,100));
  const ocrScreenshot=await send('Page.captureScreenshot',{format:'png'});await writeFile(path.join(artifact,'ocr-narrow.png'),Buffer.from(ocrScreenshot.data,'base64'));
  assert.ok(await evaluate('document.getElementById("ocrPanel").getBoundingClientRect().width<=innerWidth'));
  await send('Emulation.setDeviceMetricsOverride',{width:1000,height:760,deviceScaleFactor:1,mobile:false});
  const spanish = await evaluate(`(()=>{const c=document.createElement('canvas');c.width=1224;c.height=1584;const x=c.getContext('2d');x.fillStyle='white';x.fillRect(0,0,c.width,c.height);x.fillStyle='black';x.font='40px Arial';x.fillText('Los documentos se leen sin conexión.',90,180);x.fillText('La información permanece en este dispositivo.',90,260);x.fillText('El niño lee un libro en español.',90,340);return c.toDataURL('image/jpeg',.95).split(',')[1];})()`);
  await writeFile(path.join(fixtures,'scanned-spanish.pdf'),makeImagePdf(Buffer.from(spanish,'base64'),1224,1584));
  await open('scanned-spanish.pdf');await wait('__messages.some(m=>m.type==="state")');
  assert.equal(await evaluate('document.getElementById("ocrText").value'),'');
  await command('ocr');await evaluate('document.getElementById("ocrLanguage").value="spa";document.getElementById("ocrRead").click()');
  await wait('document.getElementById("ocrText").value.includes("conexión")','real OCR of scanned Spanish text',60000);
  assert.ok(await evaluate('document.getElementById("ocrText").value.includes("niño")'));
  await wait('__ocrWorkers.size===0');pass('Spanish OCR preserves accents and document changes clear old text');
  await evaluate('document.getElementById("ocrRead").click()');await wait('__ocrWorkers.size===1');
  await command('close');await wait('__ocrWorkers.size===0');
  assert.equal(await evaluate('document.getElementById("ocrText").value'),'');pass('closing during OCR cancels work and clears text');
  await open('reading-notes.pdf');await wait('__messages.some(m=>m.type==="state")');
  await command('thumbnail',{page:1});await wait('__messages.some(m=>m.type==="thumbnail"&&m.data.length>100)');pass('render a thumbnail on demand');
  await command('presentation',{value:true});assert.equal(await evaluate('document.body.classList.contains("presentation")'),true);await command('presentation',{value:false});pass('enter and exit presentation');
  await command('print',{pages:[1,2]});await wait('__messages.some(m=>m.type==="printReady")','prepare print pages');
  assert.equal(await evaluate('document.querySelectorAll(".printPage img").length'),2);
  const printed=await send('Page.printToPDF',{printBackground:true,preferCSSPageSize:true,displayHeaderFooter:false});await writeFile(path.join(artifact,'print-output.pdf'),Buffer.from(printed.data,'base64'));
  const printedCount=await evaluate(`(async()=>{const data=Uint8Array.from(atob(${JSON.stringify(printed.data)}),c=>c.charCodeAt(0));const task=pdfjsLib.getDocument({data});const doc=await task.promise;const count=doc.numPages;await task.destroy();return count;})()`);
  assert.equal(printedCount,2);pass('prepare and print exactly two pages to a real PDF');
  await command('cancelPrint');assert.equal(await evaluate('document.querySelectorAll(".printPage").length'),0);pass('release print resources on cancellation');
  await open('reading-notes.pdf',{page:3,scale:'1.25',rotation:180});await wait('__messages.some(m=>m.type==="state"&&m.page===3&&m.zoom===1.25&&m.rotation===180)');pass('restore page, zoom and rotation');
  await open('password.pdf');await wait('__messages.some(m=>m.type==="password"&&!m.incorrect)');await command('password',{value:'wrong'});await wait('__messages.some(m=>m.type==="password"&&m.incorrect)');await command('password',{value:'folio-test'});await wait('__messages.some(m=>m.type==="loaded")');pass('password request, incorrect password and successful unlock');
  await open('restricted.pdf');await wait('__messages.some(m=>m.type==="password")');await command('password',{value:'folio-test'});await wait('__messages.some(m=>m.type==="state")');assert.equal(await evaluate('__messages.find(m=>m.type==="loaded").canPrint'),false);pass('honor printing permissions');
  await command('ocr');assert.equal(await evaluate('document.getElementById("ocrRead").disabled'),true);assert.equal(await evaluate('__ocrWorkers.size'),0);pass('OCR honors copy restrictions');
  await open('corrupted.pdf');await wait('__messages.some(m=>m.type==="error")');pass('corrupted PDF recovery');
  await open('long-document.pdf');await wait('__messages.some(m=>m.type==="state")');await command('page',{value:120});await wait('__messages.filter(m=>m.type==="state").at(-1)?.page===120');await new Promise(r=>setTimeout(r,300));
  assert.ok(await evaluate('document.querySelectorAll(".page canvas").length')<25);pass('long-document navigation with bounded page canvases');
  await open('reading-notes.pdf');await wait('__messages.some(m=>m.type==="state")');await wait('document.querySelector(".textLayer span")?.textContent?.length>0');
  await command('theme',{value:'dark'});await send('Emulation.setDeviceMetricsOverride',{width:420,height:760,deviceScaleFactor:1,mobile:false});
  await new Promise(r=>setTimeout(r,300));const screenshot=await send('Page.captureScreenshot',{format:'png'});await writeFile(path.join(artifact,'narrow-canvas.png'),Buffer.from(screenshot.data,'base64'));pass('narrow viewport and dark canvas');
  await command('close');await wait('document.querySelectorAll(".page").length===0');pass('close releases all page views');
  await open('reading-notes.pdf');await command('close');await open('reading-notes.pdf');await wait('__messages.some(m=>m.type==="state")');pass('rapid open-close-open cannot lose the document session');
  const errors=await evaluate('__errors');assert.deepEqual(errors,[]);pass('no uncaught renderer exceptions');
  console.log('All viewer integration checks passed.');
}catch(error){failed=true;console.error(error.stack);}finally{socket?.close();browser.kill();await new Promise(resolve=>server.close(resolve));}
if(failed)process.exitCode=1;
