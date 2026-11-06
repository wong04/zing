// The worker pdf.js is given: the guard first, then pdf.js's own worker, which starts itself when it is loaded.
import './pdfjsGuard'
import 'pdfjs-dist/build/pdf.worker.min.mjs'
