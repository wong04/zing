// The defaults the C# host needs that are defined in TypeScript: settings,
// the empty library and the command list. Exported as JSON so there's one
// source of truth. `src/shared/hostDefaults.test.ts` fails if the committed
// files drift; regenerate with `npm run gen:host-defaults`.
import { SEARCH_URLS } from '../src/shared/browser/searchEngines'
import { COMMANDS } from '../src/shared/commands/registry'
import { emptyLibrary } from '../src/shared/library/library'
import { CHROME_LAYOUT } from '../src/shared/ipc'
import { DEFAULT_SETTINGS } from '../src/shared/settings/schema'
import { SIDEBAR_COLUMN_WIDTH } from '../src/shared/shell/shellState'

export function hostDefaults(): Record<string, unknown> {
	// The Favourites folder's updatedAt is the only time-dependent field; pin it so the file is stable.
	const library = emptyLibrary()
	const stableLibrary = { ...library, bookmarks: library.bookmarks.map((node) => ({ ...node, updatedAt: 0 })) }
	return {
		'settings.json': DEFAULT_SETTINGS,
		'library.json': stableLibrary,
		'commands.json': COMMANDS.map(({ id, label }) => ({ id, label })),
		'search-engines.json': SEARCH_URLS,
		'layout.json': { ...CHROME_LAYOUT, sidebarColumnWidth: SIDEBAR_COLUMN_WIDTH },
	}
}
