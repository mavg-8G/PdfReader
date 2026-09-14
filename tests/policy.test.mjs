import test from 'node:test';
import assert from 'node:assert/strict';
import {clampPage,normalizeScale,classifyError,validatePrintPages} from '../PdfReader/Viewer/policy.mjs';
test('page input is clamped and normalized',()=>{assert.equal(clampPage(-1,10),1);assert.equal(clampPage(90,10),10);assert.equal(clampPage('3.9',10),3);assert.equal(clampPage('x',10),1);});
test('zoom presets and numeric boundaries',()=>{assert.equal(normalizeScale('page-width'),'page-width');assert.equal(normalizeScale('page-fit'),'page-fit');assert.equal(normalizeScale(9),5);assert.equal(normalizeScale(.01),.25);assert.equal(normalizeScale('1.5'),1.5);assert.equal(normalizeScale(NaN),1);});
test('damaged, encrypted and missing PDFs have recovery messages',()=>{for(const name of ['InvalidPDFException','PasswordException','MissingPDFException']){const result=classifyError({name});assert.ok(result.title);assert.ok(result.message);assert.ok(!result.message.includes('undefined'));}});
test('print page validation rejects out-of-range and excessive jobs',()=>{assert.deepEqual(validatePrintPages([3,1,1],4),[1,3]);for(const pages of [[],[0],[5],[1.1],Array(101).fill(1)])assert.throws(()=>validatePrintPages(pages,4));});
