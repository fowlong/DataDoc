// Bundle entry point — re-exports everything the designer HTML needs
// as a single global (window.PdfmeBundle)

export { Designer, Form, Viewer } from '@pdfme/ui';
export { generate } from '@pdfme/generator';
export { BLANK_PDF } from '@pdfme/common';
export {
  text,
  image,
  barcodes,
  table,
  line,
  rectangle,
  ellipse,
  multiVariableText,
  svg
} from '@pdfme/schemas';
