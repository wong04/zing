import { ipcMain, type IpcMainInvokeEvent } from 'electron'
import { isCommandId, type CommandId } from '@shared/commands'
import type { InvokeChannel, InvokeContract, MenuAnchor, TitleBarColors } from '@shared/ipc'
import type { TabId } from '@shared/model'
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

const HANDLERS: { readonly [C in InvokeChannel]: Handler<C> } = {
	'command:run': (w, [id]) => w.run(commandId('command:run', id)),
	'tabs:get': (w) => w.tabs.state(),
	'tabs:activate': (w, [id]) => w.tabs.activate(tabId('tabs:activate', id)),
	'tabs:close': (w, [id]) => w.tabs.close(tabId('tabs:close', id)),
	'tabs:navigate': (w, [id, url]) => w.navigate(tabId('tabs:navigate', id), text('tabs:navigate', url, 'url')),
	'tabs:move': (w, [id, to]) => w.tabs.move(tabId('tabs:move', id), index('tabs:move', to)),
	'tabs:pin': (w, [id]) => w.tabs.pin(tabId('tabs:pin', id)),
	'tabs:unpin': (w, [id]) => w.tabs.unpin(tabId('tabs:unpin', id)),
	'chrome:titlebar-colors': (w, [c]) => w.setTitleBarColors(colors('chrome:titlebar-colors', c)),
	'chrome:app-menu': (w, [a]) => w.popupAppMenu(anchor('chrome:app-menu', a)),
	'chrome:tab-menu': (w, [id, a]) => w.popupTabMenu(tabId('chrome:tab-menu', id), anchor('chrome:tab-menu', a)),
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
