import { ipcMain, type IpcMainInvokeEvent } from 'electron'
import { isCommandId, type CommandId } from '@shared/commands'
import { tabOrganizations, type TabOrganization } from '@shared/browser/tabOrganizer'
import type { FindDirection, HistoryDirection } from '@shared/browser/pageUi'
import type {
	DragPresenceSource,
	InvokeChannel,
	InvokeContract,
	LibraryAction,
	LibraryOpenTarget,
	LibraryPanel,
	MenuAnchor,
	TitleBarColors,
} from '@shared/ipc'
import type { TabId } from '@shared/model'
import { isSettingsCategory, type Point, type SettingsCategoryId, type ShellMenuKind } from '@shared/shell/shellState'
import { openDefaultAppsSettings } from './default-browser'
import { InvalidLibraryActionError, parseLibraryAction } from './library-store'
import type { ZingWindow } from './zing-window'

export class InvalidIpcArgumentError extends Error {
	constructor(channel: string, detail: string) {
		super(`${channel}: ${detail}`)
		this.name = 'InvalidIpcArgumentError'
	}
}

export class UntrustedSenderError extends Error {
	constructor(channel: string) {
		super(`${channel}: sender is not a Zing chrome or overlay renderer`)
		this.name = 'UntrustedSenderError'
	}
}

type Args = readonly unknown[]
type Handler<C extends InvokeChannel> = (
	window: ZingWindow,
	args: Args,
) => InvokeContract[C]['result'] | Promise<InvokeContract[C]['result']>

function text(channel: string, value: unknown, name: string): string {
	if (typeof value !== 'string') throw new InvalidIpcArgumentError(channel, `${name} must be a string`)
	return value
}

function tabId(channel: string, value: unknown): TabId {
	return text(channel, value, 'tab id')
}

function commandId(channel: string, value: unknown): CommandId {
	if (!isCommandId(value)) throw new InvalidIpcArgumentError(channel, `unknown command ${String(value)}`)
	return value
}

function index(channel: string, value: unknown): number {
	if (!Number.isInteger(value)) throw new InvalidIpcArgumentError(channel, 'index must be an integer')
	return value as number
}

function point(channel: string, value: unknown): Point {
	const { x, y } = (value ?? {}) as Partial<Point>
	if (!Number.isFinite(x) || !Number.isFinite(y)) throw new InvalidIpcArgumentError(channel, 'point needs x and y')
	return { x: x!, y: y! }
}

function category(channel: string, value: unknown): SettingsCategoryId | undefined {
	if (value === undefined) return undefined
	if (!isSettingsCategory(value)) throw new InvalidIpcArgumentError(channel, `unknown settings category ${String(value)}`)
	return value
}

function menuKind(channel: string, value: unknown): ShellMenuKind {
	if (value !== 'recent' && value !== 'minimized') throw new InvalidIpcArgumentError(channel, 'unknown menu kind')
	return value
}

function bool(channel: string, value: unknown, name: string): boolean {
	if (typeof value !== 'boolean') throw new InvalidIpcArgumentError(channel, `${name} must be a boolean`)
	return value
}

function organization(channel: string, value: unknown): TabOrganization {
	if (!tabOrganizations.includes(value as TabOrganization)) throw new InvalidIpcArgumentError(channel, 'unknown tab organization')
	return value as TabOrganization
}

function flag(channel: string, value: unknown, name: string): boolean {
	if (typeof value !== 'boolean') throw new InvalidIpcArgumentError(channel, `${name} must be a boolean`)
	return value
}

function dragSource(channel: string, value: unknown): DragPresenceSource {
	if (value !== 'chip' && value !== 'panel') throw new InvalidIpcArgumentError(channel, 'source must be chip or panel')
	return value
}

function anchorX(channel: string, value: unknown): number {
	if (!Number.isFinite(value)) throw new InvalidIpcArgumentError(channel, 'anchorX must be a number')
	return value as number
}

function integer(channel: string, value: unknown, name: string): number {
	if (!Number.isInteger(value)) throw new InvalidIpcArgumentError(channel, `${name} must be an integer`)
	return value as number
}

function oneOf<T extends string>(channel: string, value: unknown, allowed: readonly T[], name: string): T {
	if (!allowed.includes(value as T)) throw new InvalidIpcArgumentError(channel, `${name} must be one of ${allowed.join(', ')}`)
	return value as T
}

function anchor(channel: string, value: unknown): MenuAnchor {
	const { x, y } = (value ?? {}) as Partial<MenuAnchor>
	if (!Number.isFinite(x) || !Number.isFinite(y)) throw new InvalidIpcArgumentError(channel, 'anchor needs x and y')
	return { x: x!, y: y! }
}

function colors(channel: string, value: unknown): TitleBarColors {
	const symbolColor = text(channel, (value as Partial<TitleBarColors> | null)?.symbolColor, 'symbolColor')
	if (!/^#[0-9a-f]{6}$/i.test(symbolColor)) throw new InvalidIpcArgumentError(channel, 'symbolColor must be #RRGGBB')
	return { symbolColor }
}

function libraryAction(channel: string, value: unknown): LibraryAction {
	try {
		return parseLibraryAction(value)
	} catch (error) {
		if (!(error instanceof InvalidLibraryActionError)) throw error
		throw new InvalidIpcArgumentError(channel, error.message)
	}
}

function openTarget(channel: string, value: unknown): LibraryOpenTarget {
	if (value !== 'current' && value !== 'tab') throw new InvalidIpcArgumentError(channel, 'target must be current or tab')
	return value
}

function libraryPanel(channel: string, value: unknown): LibraryPanel | null {
	if (value !== null && value !== 'bookmarks' && value !== 'readingList') throw new InvalidIpcArgumentError(channel, 'unknown panel')
	return value
}

const HANDLERS: { readonly [C in InvokeChannel]: Handler<C> } = {
	'command:run': (w, [id]) => w.run(commandId('command:run', id)),
	'tabs:get': (w) => w.state(),
	'tabs:activate': (w, [id]) => w.tabs.activate(tabId('tabs:activate', id)),
	'tabs:close': (w, [id]) => w.tabs.close(tabId('tabs:close', id)),
	'tabs:navigate': (w, [id, url]) => w.navigate(tabId('tabs:navigate', id), text('tabs:navigate', url, 'url')),
	'tabs:move': (w, [id, to]) => w.ordering.move(tabId('tabs:move', id), index('tabs:move', to)),
	'tabs:pin': (w, [id]) => w.tabs.pin(tabId('tabs:pin', id)),
	'tabs:unpin': (w, [id]) => w.tabs.unpin(tabId('tabs:unpin', id)),
	'tabs:move-stack': (w, [key, to]) => w.ordering.moveStack(text('tabs:move-stack', key, 'key'), index('tabs:move-stack', to)),
	'tabs:move-within': (w, [id, key, to]) =>
		w.ordering.moveWithin(tabId('tabs:move-within', id), text('tabs:move-within', key, 'key'), index('tabs:move-within', to)),
	'tabs:close-others': (w, [id]) => w.closeOtherTabs(tabId('tabs:close-others', id)),
	'tabs:set-organization': (w, [o]) => w.ordering.setOrganization(organization('tabs:set-organization', o)),
	'tabs:set-dropdowns': (w, [on]) => w.ordering.setDropdowns(flag('tabs:set-dropdowns', on, 'on')),
	'stack:toggle': (w, [key, x]) => w.stacks.toggle(text('stack:toggle', key, 'key'), anchorX('stack:toggle', x)),
	'stack:open-for-drag': (w, [key, x]) =>
		w.stacks.openForDrag(text('stack:open-for-drag', key, 'key'), anchorX('stack:open-for-drag', x)),
	'stack:close': (w) => w.stacks.close(),
	'stack:current': (w) => w.stacks.current(),
	'stack:drag-presence': (w, [source, inside]) =>
		w.stacks.dragPresence(dragSource('stack:drag-presence', source), flag('stack:drag-presence', inside, 'inside')),
	'chrome:stack-menu': (w, [key, a]) => w.popupStackMenu(text('chrome:stack-menu', key, 'key'), anchor('chrome:stack-menu', a)),
	'chrome:strip-menu': (w, [a]) => w.popupStripMenu(anchor('chrome:strip-menu', a)),
	'chrome:history-menu': (w, [d, a]) =>
		w.features.popupHistoryMenu(
			oneOf<HistoryDirection>('chrome:history-menu', d, ['back', 'forward'], 'direction'),
			anchor('chrome:history-menu', a),
		),
	'pageui:get': (w) => w.features.panels.state(),
	'pageui:height': (w, [h]) => w.features.panels.setHeight(integer('pageui:height', h, 'height')),
	'find:update': (w, [t]) => w.features.findUpdate(text('find:update', t, 'text')),
	'find:step': (w, [d]) => w.features.findStep(oneOf<FindDirection>('find:step', d, ['next', 'previous'], 'direction')),
	'find:close': (w) => w.features.findClose(),
	'downloads:open': (w, [id]) => w.features.downloads.open(integer('downloads:open', id, 'id')),
	'downloads:show': (w, [id]) => w.features.downloads.show(integer('downloads:show', id, 'id')),
	'downloads:cancel': (w, [id]) => w.features.downloads.cancel(integer('downloads:cancel', id, 'id')),
	'downloads:clear': (w) => w.features.downloads.clear(),
	'downloads:dismiss': (w) => w.features.downloads.dismiss(),
	'permission:allow': (w, [id]) => w.features.permissions.answer(integer('permission:allow', id, 'id'), true),
	'permission:deny': (w, [id]) => w.features.permissions.answer(integer('permission:deny', id, 'id'), false),
	'chrome:titlebar-colors': (w, [c]) => w.setTitleBarColors(colors('chrome:titlebar-colors', c)),
	'chrome:app-menu': (w, [a]) => w.popupAppMenu(anchor('chrome:app-menu', a)),
	'default-browser:open-settings': () => openDefaultAppsSettings(),
	'settings:get': (w) => w.shell.settingsSnapshot(),
	'settings:set': (w, [key, value]) => w.shell.setSetting(text('settings:set', key, 'key'), value),
	'shell:get': (w) => w.shell.state(),
	'shell:open-settings': (w, [c]) => w.shell.openSettings(category('shell:open-settings', c)),
	'shell:close-settings': (w) => w.shell.closeSettings(),
	'shell:toolbox': (w) => w.shell.openToolbox(),
	'shell:menu': (w, [kind, id, a]) =>
		w.shell.popupMenu(menuKind('shell:menu', kind), text('shell:menu', id, 'id'), anchor('shell:menu', a)),
	'shell:clear-session': (w) => w.shell.clearBrowsingSession(),
	'toolpanel:get': (w) => w.shell.toolPanels(),
	'toolpanel:open': (w, [content, at]) =>
		w.shell.openPanel(text('toolpanel:open', content, 'content'), point('toolpanel:open', at)),
	'toolpanel:close': (w, [id]) => w.shell.closePanel(text('toolpanel:close', id, 'id')),
	'toolpanel:minimize': (w, [id]) => w.shell.minimizePanel(text('toolpanel:minimize', id, 'id')),
	'toolpanel:restore': (w, [id]) => w.shell.restorePanel(text('toolpanel:restore', id, 'id')),
	'toolpanel:focus': (w, [id]) => w.shell.focusPanel(text('toolpanel:focus', id, 'id')),
	'toolpanel:begin-drag': (w, [id]) => w.shell.beginPanelDrag(text('toolpanel:begin-drag', id, 'id')),
	'toolpanel:end-drag': (w, [id, origin]) =>
		w.shell.endPanelDrag(text('toolpanel:end-drag', id, 'id'), point('toolpanel:end-drag', origin)),
	'account:get': (w) => w.account.state(),
	'account:sign-up': (w, [u, d, e, p]) => {
		const args = [u, d, e, p].map((v, i) => text('account:sign-up', v, ['username', 'displayName', 'email', 'password'][i]!))
		return w.shell.accountAction(() => w.account.signUp(args[0]!, args[1]!, args[2]!, args[3]!))
	},
	'account:sign-in': (w, [p]) => {
		const password = text('account:sign-in', p, 'password')
		return w.shell.accountAction(() => w.account.signIn(password))
	},
	'account:reset-password': (w, [p]) => {
		const password = text('account:reset-password', p, 'password')
		return w.shell.accountAction(() => w.account.resetPassword(password))
	},
	'account:sign-out': (w) => w.shell.accountAction(() => w.account.signOut()),
	'account:continue-as-guest': (w) => w.shell.accountAction(() => w.account.continueAsGuest()),
	'account:update-profile': (w, [d, e]) => {
		const name = text('account:update-profile', d, 'displayName')
		const email = text('account:update-profile', e, 'email')
		return w.shell.accountAction(() => w.account.updateProfile(name, email))
	},
	'account:require-password': (w, [r]) => {
		const required = bool('account:require-password', r, 'required')
		return w.shell.accountAction(() => w.account.setRequirePasswordOnLaunch(required))
	},
	'account:delete': (w) => w.shell.accountAction(() => w.account.deleteAccount()),
	'chrome:tab-menu': (w, [id, a]) => w.popupTabMenu(tabId('chrome:tab-menu', id), anchor('chrome:tab-menu', a)),
	'library:get': (w) => w.library.state(),
	'library:apply': (w, [action]) => w.library.apply(libraryAction('library:apply', action)),
	'library:open': (w, [id, target]) => w.library.open(text('library:open', id, 'id'), openTarget('library:open', target)),
	'library:open-all': (w, [id]) => w.library.openAll(text('library:open-all', id, 'id')),
	'library:panel': (w, [panel]) => w.library.setPanel(libraryPanel('library:panel', panel)),
	'library:folder-menu': (w, [id, a]) => w.library.popupFolderMenu(text('library:folder-menu', id, 'id'), anchor('library:folder-menu', a)),
	'library:item-menu': (w, [id, a]) => w.library.popupItemMenu(text('library:item-menu', id, 'id'), anchor('library:item-menu', a)),
	'overview:get': (w) => w.overview.current(),
	'overview:close': (w) => w.overview.close(),
}

/**
 * Registers every invoke channel once. Each call is answered only for a
 * sender that is one of a window's own chrome or overlay renderers; page
 * views have no preload, but the check doesn't rely on that alone.
 */
export function registerIpcHandlers(windowFor: (event: IpcMainInvokeEvent) => ZingWindow | undefined): void {
	for (const channel of Object.keys(HANDLERS) as InvokeChannel[]) {
		ipcMain.handle(channel, (event, ...args: unknown[]) => {
			const window = windowFor(event)
			if (!window?.isTrusted(event.sender)) throw new UntrustedSenderError(channel)
			return (HANDLERS[channel] as Handler<typeof channel>)(window, args)
		})
	}
}
