import { defineConfig } from 'vite';
import { resolve } from 'path';

export default defineConfig({
  build: {
    lib: {
      entry: resolve(__dirname, 'src/index.js'),
      name: 'PdfmeBundle',
      formats: ['iife'],
      fileName: () => 'pdfme-bundle.js'
    },
    outDir: resolve(__dirname, '..'),
    emptyOutDir: false,
    minify: 'esbuild',
    rollupOptions: {
      output: {
        inlineDynamicImports: true
      }
    }
  }
});
