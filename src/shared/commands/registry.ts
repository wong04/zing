import { shortcutMatches, type KeyInput, type Shortcut } from './shortcut'

/**
 * Every command Krackn's Commands.swift exposes, with Windows keys. Menus,
 * keyboard shortcuts, toolbar buttons and the roadmap Ctrl+K bar all route
 * through these ids, so an id must never change once shipped.
 */
export const COMMAND_IDS = [
	'tab.new',
	'window.new',
	'location.open',
	'tab.close',
	'window.close',
	'tab.reopenClosed',
	'page.find',
	'page.findNext',
	'page.findPrevious',
	'page.reload',
	'page.stop',
	'history.back',
	'history.forward',
	'history.home',
	'page.zoomIn',
	'page.zoomOut',
	'page.actualSize',
	'page.textBigger',
	'page.textSmaller',
	'page.print',
	'page.reader',
	'tabs.overview',
	'tab.next',
	'tab.previous',
	'tab.select1',
	'tab.select2',
	'tab.select3',
	'tab.select4',
	'tab.select5',
	'tab.select6',
	'tab.select7',
	'tab.select8',
	'tab.select9',
	'library.bookmarks',
	'library.readingList',
	'library.addFavourite',
	'library.addReadingList',
	'view.favouritesBar',
	'view.sidebar',
	'app.settings',
] as const

export type CommandId = (typeof COMMAND_IDS)[number]

export interface CommandDefinition {
	readonly id: CommandId
	readonly label: string
	/** The first is shown in menus and tooltips; the rest are alternates (Krackn's `AlternateShortcuts`). */
	readonly shortcuts: readonly Shortcut[]
}

const ctrl = (code: Shortcut['code']): Shortcut => ({ code, ctrl: true })
const ctrlShift = (code: Shortcut['code']): Shortcut => ({ code, ctrl: true, shift: true })
const alt = (code: Shortcut['code']): Shortcut => ({ code, alt: true })
const shift = (code: Shortcut['code']): Shortcut => ({ code, shift: true })
const bare = (code: Shortcut['code']): Shortcut => ({ code })

const SELECT_TAB_COMMANDS = ([1, 2, 3, 4, 5, 6, 7, 8, 9] as const).map(
	(n): CommandDefinition => ({
		id: `tab.select${n}`,
		label: n === 9 ? 'Show Last Tab' : `Show Tab ${n}`,
		shortcuts: [ctrl(`Digit${n}`)],
	}),
)

export const COMMANDS: readonly CommandDefinition[] = [
	{ id: 'tab.new', label: 'New Tab', shortcuts: [ctrl('KeyT')] },
	{ id: 'window.new', label: 'New Window', shortcuts: [ctrl('KeyN')] },
	{ id: 'location.open', label: 'Open Location…', shortcuts: [ctrl('KeyL'), alt('KeyD'), bare('F6')] },
	{ id: 'tab.close', label: 'Close Tab', shortcuts: [ctrl('KeyW'), ctrl('F4')] },
	{ id: 'window.close', label: 'Close Window', shortcuts: [ctrlShift('KeyW')] },
	{ id: 'tab.reopenClosed', label: 'Reopen Last Closed Tab', shortcuts: [ctrlShift('KeyT')] },
	{ id: 'page.find', label: 'Find…', shortcuts: [ctrl('KeyF')] },
	{ id: 'page.findNext', label: 'Find Next', shortcuts: [bare('F3'), ctrl('KeyG')] },
	{ id: 'page.findPrevious', label: 'Find Previous', shortcuts: [shift('F3'), ctrlShift('KeyG')] },
	{ id: 'page.reload', label: 'Reload Page', shortcuts: [bare('F5'), ctrl('KeyR')] },
	{ id: 'page.stop', label: 'Stop', shortcuts: [bare('Escape')] },
	{ id: 'history.back', label: 'Back', shortcuts: [alt('ArrowLeft')] },
	{ id: 'history.forward', label: 'Forward', shortcuts: [alt('ArrowRight')] },
	{ id: 'history.home', label: 'Home', shortcuts: [alt('Home')] },
	{
		id: 'page.zoomIn',
		label: 'Zoom In',
		shortcuts: [ctrl('Equal'), ctrlShift('Equal'), ctrl('NumpadAdd')],
	},
	{ id: 'page.zoomOut', label: 'Zoom Out', shortcuts: [ctrl('Minus'), ctrl('NumpadSubtract')] },
	{ id: 'page.actualSize', label: 'Actual Size', shortcuts: [ctrl('Digit0'), ctrl('Numpad0')] },
	// Office's grow/shrink-font keys. Ctrl+Alt is AltGr on many layouts (German AltGr+ß types a backslash).
	{ id: 'page.textBigger', label: 'Make Text Bigger', shortcuts: [ctrlShift('Period')] },
	{ id: 'page.textSmaller', label: 'Make Text Smaller', shortcuts: [ctrlShift('Comma')] },
	{ id: 'page.print', label: 'Print…', shortcuts: [ctrl('KeyP')] },
	// F9 is Edge's Immersive Reader key; Ctrl+Shift+R is a hard reload on Windows.
	{ id: 'page.reader', label: 'Show Reader', shortcuts: [bare('F9')] },
	{ id: 'tabs.overview', label: 'Show Tab Overview', shortcuts: [ctrlShift('Backslash')] },
	{ id: 'tab.next', label: 'Show Next Tab', shortcuts: [ctrl('Tab'), ctrl('PageDown')] },
	{ id: 'tab.previous', label: 'Show Previous Tab', shortcuts: [ctrlShift('Tab'), ctrl('PageUp')] },
	...SELECT_TAB_COMMANDS,
	{ id: 'library.bookmarks', label: 'Show Bookmarks', shortcuts: [ctrlShift('KeyO')] },
	{ id: 'library.readingList', label: 'Show Reading List', shortcuts: [ctrlShift('KeyY')] },
	{ id: 'library.addFavourite', label: 'Add to Favourites', shortcuts: [ctrl('KeyD')] },
	{ id: 'library.addReadingList', label: 'Add to Reading List', shortcuts: [ctrlShift('KeyD')] },
	{ id: 'view.favouritesBar', label: 'Toggle Favourites Bar', shortcuts: [ctrlShift('KeyB')] },
	{ id: 'view.sidebar', label: 'Toggle Sidebar', shortcuts: [ctrlShift('KeyS')] },
	{ id: 'app.settings', label: 'Settings…', shortcuts: [ctrl('Comma')] },
]

const BY_ID = new Map(COMMANDS.map((c) => [c.id, c]))

export class UnknownCommandError extends Error {
	constructor(readonly commandId: string) {
		super(`Unknown command: ${commandId}`)
		this.name = 'UnknownCommandError'
	}
}

export function isCommandId(value: unknown): value is CommandId {
	return typeof value === 'string' && BY_ID.has(value as CommandId)
}

export function getCommand(id: CommandId): CommandDefinition {
	const command = BY_ID.get(id)
	if (!command) throw new UnknownCommandError(id)
	return command
}

export function commandForInput(input: KeyInput): CommandDefinition | undefined {
	return COMMANDS.find((c) => c.shortcuts.some((s) => shortcutMatches(s, input)))
}

export type MenuEntry = CommandId | 'separator'

export interface MenuSection {
	readonly label: string
	readonly items: readonly MenuEntry[]
}

/** Menu layout, following Commands.swift's groups. The Edit menu's clipboard roles are added by main. */
export const MENU_LAYOUT: readonly MenuSection[] = [
	{
		label: 'File',
		items: [
			'tab.new',
			'window.new',
			'separator',
			'location.open',
			'separator',
			'tab.close',
			'window.close',
			'tab.reopenClosed',
			'separator',
			'page.print',
			'separator',
			'app.settings',
		],
	},
	{ label: 'Edit', items: ['page.find', 'page.findNext', 'page.findPrevious'] },
	{
		label: 'View',
		items: [
			'view.sidebar',
			'view.favouritesBar',
			'separator',
			'tabs.overview',
			'page.reader',
			'separator',
			'page.reload',
			'page.stop',
			'separator',
			'page.zoomIn',
			'page.zoomOut',
			'page.actualSize',
			'page.textBigger',
			'page.textSmaller',
		],
	},
	{
		label: 'History',
		items: ['history.back', 'history.forward', 'history.home', 'separator', 'tab.reopenClosed'],
	},
	{
		label: 'Bookmarks',
		items: [
			'library.addFavourite',
			'library.addReadingList',
			'separator',
			'library.bookmarks',
			'library.readingList',
		],
	},
	{ label: 'Window', items: ['tab.previous', 'tab.next'] },
]
