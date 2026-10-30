import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'
import { app, desktopCapturer, nativeTheme, screen } from 'electron'
import type { CommandId } from '@shared/commands'
import type { ZingWindow } from './zing-window'

const SETTLE_MS = 2500

/**
 * Dev-only evidence capture, off unless ZING_SCREENSHOT=<png path> is set.
 * Captures through the desktop compositor rather than `capturePage`, which
 * would miss the page views, the native caption buttons and Mica.
 * ZING_SCREENSHOT_URLS (comma-separated) opens tabs first; ZING_SCREENSHOT_THEME
 * forces light or dark for the capture only.
 */
export function captureScreenshotOnLoad(target: ZingWindow): void {
	const path = process.env['ZING_SCREENSHOT']
	if (!path) return
	const theme = process.env['ZING_SCREENSHOT_THEME']
	if (theme === 'light' || theme === 'dark') nativeTheme.themeSource = theme
	const urls = (process.env['ZING_SCREENSHOT_URLS'] ?? '').split(',').filter(Boolean)
	target.window.webContents.once('did-finish-load', () => {
		const firstId = target.tabs.activeId
		const [first, ...rest] = urls
		for (const url of rest) target.tabs.open(url)
		if (first && firstId) {
			target.tabs.activate(firstId)
			target.navigate(firstId, first)
		}
		// ZING_SCREENSHOT_COMMANDS (comma-separated command ids, e.g. app.settings) runs before the capture.
		for (const id of (process.env['ZING_SCREENSHOT_COMMANDS'] ?? '').split(',').filter(Boolean)) target.run(id as CommandId)
		setTimeout(() => void capture(target, path), SETTLE_MS)
	})
}

async function capture(target: ZingWindow, path: string): Promise<void> {
	const { width, height } = target.window.getBounds()
	const scale = screen.getDisplayMatching(target.window.getBounds()).scaleFactor
	const sources = await desktopCapturer.getSources({
		types: ['window'],
		thumbnailSize: { width: Math.round(width * scale), height: Math.round(height * scale) },
	})
	const source = sources.find((s) => s.id === target.window.getMediaSourceId())
	if (!source) throw new Error(`No desktop capture source for window ${target.window.getMediaSourceId()}`)
	mkdirSync(dirname(path), { recursive: true })
	writeFileSync(path, source.thumbnail.toPNG())
	app.quit()
}
