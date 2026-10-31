import { defineConfig } from '@playwright/test'

// Drives the WebView2 host (host/Zing.Host) over CDP. Run with
// `npm run test:e2e`, which builds the renderer and the host first.
export default defineConfig({
	testDir: 'e2e',
	timeout: 90_000,
	// CI runners start WebView2 and finish downloads several times slower than a desk PC.
	expect: { timeout: 15_000 },
	workers: 1,
	reporter: 'list',
})
