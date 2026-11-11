import type { BookmarkImportOutcome } from '@shared/ipc'

const bookmarks = (n: number): string => `${n} ${n === 1 ? 'bookmark' : 'bookmarks'}`

/** One sentence on what a bookmark import did. */
export function outcomeLine(o: BookmarkImportOutcome): string {
	const skipped = o.skipped.length
	if (o.addedToFolder === 0 && o.addedToBar === 0) return skipped > 0 ? `Nothing new to import; skipped ${skipped}.` : 'Nothing new to import.'
	const parts =
		o.addedToFolder > 0
			? [`Added ${bookmarks(o.addedToFolder)} to ${o.folderTitle}`, ...(o.addedToBar > 0 ? [`${o.addedToBar} to the favourites bar`] : [])]
			: [`Added ${bookmarks(o.addedToBar)} to the favourites bar`]
	return `${parts.join(', ')}${skipped > 0 ? `; skipped ${skipped}` : ''}.`
}

/** How to get a bookmarks file out of the browsers Zing can't read from this PC. */
export const fileHelp: readonly { app: string; steps: string }[] = [
	{ app: 'Dia', steps: 'Open the Bookmarks menu and choose Export Bookmarks. Dia saves an HTML file.' },
	{ app: 'Safari', steps: 'Choose File > Export > Bookmarks. Safari saves an HTML file.' },
	{ app: 'Firefox', steps: 'Open the Library (Ctrl+Shift+O), then Import and Backup > Export Bookmarks to HTML.' },
]

/** Shown in place of the Passwords file button while the vault is off. */
export const PASSWORDS_FILE_VAULT_OFF = 'Turn on “Offer to fill saved passwords” in Passwords to import a passwords file.'
