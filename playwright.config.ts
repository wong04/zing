import { defineConfig } from '@playwright/test'

// The real-input tier (e2e/real-input.spec.ts) puts the window on screen and drives the OS mouse and
// keyboard, so it runs only by hand on a PC nobody is using: `npm run test:e2e:real-input`, or every
// test with ZING_REAL_INPUT=1. Normal runs never send real input (AGENTS.md).
const realInput = process.env.ZING_REAL_INPUT === '1' || process.env.npm_lifecycle_event === 'test:e2e:real-input'

// Drives the WebView2 host (host/Zing.Host) over CDP. Run with
// `npm run test:e2e`, which builds the renderer and the host first.
export default defineConfig({
	testDir: 'e2e',
	timeout: 90_000,
	// CI runners start WebView2 and finish downloads several times slower than a desk PC.
	expect: { timeout: 15_000 },
	// More workers fail even on a quiet PC: Zing dies at startup in AppInstance, workers crash, and timing tests go over. The gain stops at 2 or 3.
	workers: 1,
	// The WebView2 engine sometimes keeps its profile lock for 30 s and more after Zing quits, on CI only; a rerun of the test passes.
	retries: process.env.CI ? 1 : 0,
	reporter: 'list',
	// Only a failed test's folder is kept: its pictures and logs (e2e/zing.ts `test`), which CI uploads.
	preserveOutput: 'failures-only',
	grepInvert: realInput ? undefined : /@real-input/,
})
