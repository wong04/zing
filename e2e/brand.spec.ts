// BR2: Zing's window and Zing.exe carry the logo (build/icons*, from `npm run make-icons`), and the
// window carries the tile that stands out on the taskbar Windows is in now (TaskbarIcon.cs).
// With ZING_SCREENSHOTS=1 it also draws the taskbar button, light and dark, into docs/screenshots/phase-7.
import { execFileSync } from 'node:child_process'
import { join } from 'node:path'
import { expect } from '@playwright/test'
import { HOST_EXE, launchZing, test } from './zing'

const SCRIPT = join(import.meta.dirname, 'window-icon.ps1')
const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots', 'phase-7')

interface Found { size: number; diff: { frosted: number; dark: number } }
interface Icons { taskbar: 'light' | 'dark'; big: Found; small: Found; exe: Found }

/** The shipped tile closest to an icon Windows reports, as long as it is close. */
function tileOf(found: Found): string {
	const [best, other] = Object.entries(found.diff).sort((a, b) => a[1] - b[1])
	if (!best || !other || best[1] > 6 || other[1] < best[1] * 3) return `neither (${JSON.stringify(found)})`
	return best[0]
}

test('the window and the exe carry the logo, on the tile for the current taskbar', async () => {
	const zing = await launchZing()
	try {
		const args = ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', SCRIPT, '-ProcessId', String(zing.pid), '-Exe', HOST_EXE]
		if (process.env.ZING_SCREENSHOTS === '1') args.push('-PreviewDir', OUT_DIR)
		const icons = JSON.parse(execFileSync('powershell.exe', args, { encoding: 'utf8', timeout: 60_000 })) as Icons
		// The frosted tile on a dark taskbar, the dark tile on a light one.
		const tile = icons.taskbar === 'light' ? 'dark' : 'frosted'
		expect(tileOf(icons.big)).toBe(tile)
		expect(tileOf(icons.small)).toBe(tile)
		expect(tileOf(icons.exe)).toBe('frosted')
	} finally {
		await zing.close()
	}
})
