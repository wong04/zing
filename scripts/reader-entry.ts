// Bundled by `npm run gen:reader` into host/Zing.Host/Assets/reader.js, which the WebView2 host
// evaluates in an isolated world of the page before calling __zingReader(css).
import { showReader } from '../src/shared/browser/readerPage'

;(globalThis as unknown as { __zingReader: (css: string) => string }).__zingReader = (css) => JSON.stringify(showReader(document, css))
