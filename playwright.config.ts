import { defineConfig } from '@playwright/test'

class UnsupportedNodeError extends Error {
	override name = 'UnsupportedNodeError'
}

// Node 24.15's libuv 1.51.0 calls RtlGetVersion on an OSVERSIONINFOW whose size it never set, on every TCP connect
// (uv__tcp_try_connect). When the stack left that size at sizeof(OSVERSIONINFOEXW), Windows writes past it, the /GS
// check fails and the process ends with 0xC0000409 and no output: the Playwright worker died so at random, about once
// per full run, on whichever test was starting Zing. Node 24.16.0 carries the fix (libuv aabb765, nodejs/node#62561),
// still reporting libuv 1.51.0.
function refuseBrokenLibuv(): void {
	const [major, minor] = process.versions.node.split('.').map(Number)
	if (major !== 24 || minor! >= 16 || process.versions.uv !== '1.51.0') return
	throw new UnsupportedNodeError(`Node ${process.versions.node} (libuv ${process.versions.uv}) crashes Playwright workers at random on Windows with 0xC0000409; install Node 24.16.0 or later`)
}

refuseBrokenLibuv()

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
	// Runs started in other worktrees wait for this one: they share the PC, and overlapping runs fail (scripts/run-lock.ts).
	globalSetup: './e2e/global-setup.ts',
	// No retries, on CI too: a rerun that passes hides a race (AGENTS.md). The one CI retry (#140) hid unblock.spec.ts's
	// keyboard-pick flake as well as the WebView2 profile-lock stall it was added for.
	retries: 0,
	reporter: 'list',
	// Only a failed test's folder is kept: its pictures and logs (e2e/zing.ts `test`), which CI uploads.
	preserveOutput: 'failures-only',
	grepInvert: realInput ? undefined : /@real-input/,
})
