import { readdir, readFile } from 'node:fs/promises'
import { join } from 'node:path'

export type ChromiumBrowser = 'chrome' | 'edge' | 'brave'

export interface ChromiumProfile {
	browser: ChromiumBrowser
	profileDir: string
	name: string
	bookmarksPath: string
}

// User Data folders relative to %LOCALAPPDATA%.
export const USER_DATA_DIRS: Record<ChromiumBrowser, string> = {
	chrome: join('Google', 'Chrome', 'User Data'),
	edge: join('Microsoft', 'Edge', 'User Data'),
	brave: join('BraveSoftware', 'Brave-Browser', 'User Data'),
}

// Chromium creates these itself; they're never someone's bookmarks.
const NOT_USER_PROFILES = new Set(['Guest Profile', 'System Profile'])

interface LocalState {
	profile?: { info_cache?: Record<string, { name?: string }> }
}

// The display names ("Work", "Person 1") live in `Local State`, not in the
// profile folders, whose names are just "Default", "Profile 1" and so on.
export function profileNames(localStateJson: string): Map<string, string> {
	const state = JSON.parse(localStateJson) as LocalState
	const cache = state.profile?.info_cache ?? {}
	return new Map(Object.entries(cache).map(([dir, info]) => [dir, info.name || dir]))
}

export async function findChromiumProfiles(localAppData: string): Promise<ChromiumProfile[]> {
	const found: ChromiumProfile[] = []
	for (const [browser, relative] of Object.entries(USER_DATA_DIRS) as [ChromiumBrowser, string][]) {
		const userData = join(localAppData, relative)
		const localState = await readIfExists(join(userData, 'Local State'))
		if (localState === undefined) continue
		const names = profileNames(localState)
		const dirs = await readdir(userData, { withFileTypes: true })
		for (const dir of dirs) {
			if (!dir.isDirectory() || NOT_USER_PROFILES.has(dir.name)) continue
			const bookmarksPath = join(userData, dir.name, 'Bookmarks')
			if ((await readIfExists(bookmarksPath)) === undefined) continue
			found.push({ browser, profileDir: dir.name, name: names.get(dir.name) ?? dir.name, bookmarksPath })
		}
	}
	return found
}

async function readIfExists(path: string): Promise<string | undefined> {
	try {
		return await readFile(path, 'utf8')
	} catch (error) {
		if ((error as NodeJS.ErrnoException).code === 'ENOENT') return undefined
		throw error
	}
}
