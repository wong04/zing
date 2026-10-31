// Writes the reader stylesheet next to the bundled reader script, both for the WebView2 host.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { readerCss } from '../src/shared/browser/readerArticle'

const root = join(import.meta.dirname, '..')
const tokens = readFileSync(join(root, 'src', 'renderer', 'src', 'theme', 'tokens.css'), 'utf8')
const dir = join(root, 'host', 'Zing.Host', 'Assets')
mkdirSync(dir, { recursive: true })
writeFileSync(join(dir, 'reader.css'), readerCss(tokens))
console.info('wrote host/Zing.Host/Assets/reader.css')
