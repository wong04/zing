import { defineConfig } from '@playwright/test'

// Drives the WebView2 host (host/Zing.Host) over CDP. Run with
// `npm run test:e2e:webview2`, which builds the renderer and the host first.
export default defineConfig({
	testDir: 'e2e-webview2',
	timeout: 60_000,
	workers: 1,
	reporter: 'list',
})
