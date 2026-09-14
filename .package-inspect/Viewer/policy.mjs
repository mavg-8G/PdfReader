export function clampPage(value, count) { return Math.min(Math.max(1, Math.trunc(Number(value)) || 1), Math.max(1, count)); }
export function normalizeScale(value) {
  if (['page-width','page-fit','auto'].includes(value)) return value;
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? Math.min(5, Math.max(.25, number)) : 1;
}
export function classifyError(error) {
  if (error instanceof TypeError && /fetch/i.test(error.message)) return { title:'The local document connection failed', message:'Close and reopen Folio, then try the PDF again. If the problem continues, install the latest Folio build.' };
  if (error.name === 'PasswordException') return { title:'Unable to unlock this PDF', message:'The password was not accepted. Open the document again to retry.' };
  if (error.name === 'InvalidPDFException') return { title:'This PDF could not be read', message:'The file appears damaged or incomplete. Try obtaining a new copy.' };
  if (error.name === 'MissingPDFException' || error.name === 'UnexpectedResponseException') return { title:'The PDF is no longer available', message:'The file could not be read. Open it again from your device.' };
  return { title:'Unable to open this PDF', message: String(error.message || 'This PDF uses an unsupported format or is damaged.').slice(0, 400) };
}
export function validatePrintPages(pages, count) {
  if (!Array.isArray(pages) || !pages.length || pages.length > 100 || pages.some(p => !Number.isInteger(p) || p < 1 || p > count))
    throw new Error('Choose between 1 and 100 valid pages for printing.');
  return [...new Set(pages)].sort((a,b) => a-b);
}
