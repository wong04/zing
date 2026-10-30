import { join } from 'node:path'
import type { WebContents } from 'electron'

export const PRELOAD_PATH = join(__dirname, '../preload/index.cjs')

/** The chrome and the overlay are one bundle; the hash picks which root renders. */
function loadRendererPage(contents: WebContents, hash: string): Promise<void> {
	const devServer = process.env['ELECTRON_RENDERER_URL']
	if (devServer) return contents.loadURL(`${devServer}#${hash}`)
	return contents.loadFile(join(__dirname, '../renderer/index.html'), { hash })
}

export function loadChrome(contents: WebContents): Promise<void> {
	return loadRendererPage(contents, 'chrome')
}

export function loadOverlay(contents: WebContents): Promise<void> {
	return loadRendererPage(contents, 'overlay')
}

export function loadPageUi(contents: WebContents): Promise<void> {
	return loadRendererPage(contents, 'pageui')
}

export function loadSurface(contents: WebContents): Promise<void> {
	return loadRendererPage(contents, 'surface')
}

export function loadTabOverview(contents: WebContents): Promise<void> {
	return loadRendererPage(contents, 'overlay-overview')
}

export function loadStackDropdown(contents: WebContents): Promise<void> {
	return loadRendererPage(contents, 'overlay-stack')
}
