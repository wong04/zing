import { cpSync } from 'node:fs'
import { resolve } from 'node:path'
import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'

// pdf.js reads fonts and character maps by address, and Zing allows only its own origin, so the two folders it needs are
// shipped in the bundle (out/renderer/pdfjs). They are fetched only when a PDF that needs them is drawn.
function pdfjsAssets(): Plugin {
	return {
		name: 'pdfjs-assets',
		apply: 'build',
		closeBundle() {
			for (const folder of ['cmaps', 'standard_fonts']) cpSync(resolve('node_modules/pdfjs-dist', folder), resolve('out/renderer/pdfjs', folder), { recursive: true })
		},
	}
}

// Zing's UI: one bundle with a root per view (#chrome, #overlay, #surface…). The WebView2 host
// serves out/renderer from https://app.zing/ (host/Zing.Host/RendererView.cs).
export default defineConfig({
	root: 'src/renderer',
	base: './',
	resolve: { alias: { '@shared': resolve('src/shared'), '@renderer': resolve('src/renderer/src') } },
	plugins: [react(), pdfjsAssets()],
	build: { outDir: resolve('out/renderer'), emptyOutDir: true },
})
