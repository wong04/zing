import { resolve } from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Zing's UI: one bundle with a root per view (#chrome, #overlay, #surface…). The WebView2 host
// serves out/renderer from https://app.zing/ (host/Zing.Host/RendererView.cs).
export default defineConfig({
	root: 'src/renderer',
	base: './',
	resolve: { alias: { '@shared': resolve('src/shared'), '@renderer': resolve('src/renderer/src') } },
	plugins: [react()],
	build: { outDir: resolve('out/renderer'), emptyOutDir: true },
})
