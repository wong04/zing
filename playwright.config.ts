import { defineConfig } from '@playwright/test'

// Drives the real built app through Playwright's Electron support. Run with
// `npm run test:e2e`, which builds first.
export default defineConfig({
	testDir: 'e2e',
	timeout: 60_000,
	workers: 1,
	reporter: 'list',
})
