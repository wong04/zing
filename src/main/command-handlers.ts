import type { BrowserWindow, WebContents } from 'electron'
import { getCommand, type CommandId } from '@shared/commands'
import type { CommandOutcome } from '@shared/ipc'
import { NEW_TAB_URL, zoomInFactor, zoomOutFactor, type TabId } from '@shared/model'
import type { TabService } from './tab-service'

export interface CommandContext {
	readonly tabs: TabService
	readonly window: BrowserWindow
	readonly homepage: () => string
	readonly focusAddress: () => void
}

type Status = 'done' | 'not-applicable'
type Handler = (ctx: CommandContext) => Status

function onActivePage(ctx: CommandContext, act: (contents: WebContents) => Status): Status {
	const contents = ctx.tabs.activeContents()
	return contents ? act(contents) : 'not-applicable'
}

function activateIfAny(ctx: CommandContext, id: TabId | undefined): Status {
	if (id === undefined) return 'not-applicable'
	ctx.tabs.activate(id)
	return 'done'
}

function selectTab(n: number): Handler {
	return (ctx) => activateIfAny(ctx, ctx.tabs.idForShortcut(n))
}

/**
 * What Phase 1 can actually do. Commands missing here are registered (menus
 * and shortcuts show them) but answer "not implemented yet" out loud.
 */
const HANDLERS: Partial<Record<CommandId, Handler>> = {
	'tab.new': (ctx) => {
		ctx.tabs.open(NEW_TAB_URL)
		ctx.focusAddress()
		return 'done'
	},
	'location.open': (ctx) => {
		ctx.focusAddress()
		return 'done'
	},
	'tab.close': (ctx) => {
		const id = ctx.tabs.activeId
		if (id === undefined) return 'not-applicable'
		ctx.tabs.close(id)
		return 'done'
	},
	'window.close': (ctx) => {
		ctx.window.close()
		return 'done'
	},
	'tab.reopenClosed': (ctx) => (ctx.tabs.reopenClosed() ? 'done' : 'not-applicable'),
	'page.reload': (ctx) =>
		onActivePage(ctx, (c) => {
			c.reload()
			return 'done'
		}),
	'page.stop': (ctx) =>
		onActivePage(ctx, (c) => {
			if (!c.isLoading()) return 'not-applicable'
			c.stop()
			return 'done'
		}),
	'history.back': (ctx) =>
		onActivePage(ctx, (c) => {
			if (!c.navigationHistory.canGoBack()) return 'not-applicable'
			c.navigationHistory.goBack()
			return 'done'
		}),
	'history.forward': (ctx) =>
		onActivePage(ctx, (c) => {
			if (!c.navigationHistory.canGoForward()) return 'not-applicable'
			c.navigationHistory.goForward()
			return 'done'
		}),
	'history.home': (ctx) => {
		const id = ctx.tabs.activeId
		if (id === undefined) return 'not-applicable'
		ctx.tabs.navigate(id, ctx.homepage() || NEW_TAB_URL)
		return 'done'
	},
	'page.zoomIn': (ctx) =>
		onActivePage(ctx, (c) => {
			c.setZoomFactor(zoomInFactor(c.getZoomFactor()))
			return 'done'
		}),
	'page.zoomOut': (ctx) =>
		onActivePage(ctx, (c) => {
			c.setZoomFactor(zoomOutFactor(c.getZoomFactor()))
			return 'done'
		}),
	'page.actualSize': (ctx) =>
		onActivePage(ctx, (c) => {
			c.setZoomFactor(1)
			return 'done'
		}),
	'tab.next': (ctx) => activateIfAny(ctx, ctx.tabs.nextId()),
	'tab.previous': (ctx) => activateIfAny(ctx, ctx.tabs.previousId()),
	'tab.select1': selectTab(1),
	'tab.select2': selectTab(2),
	'tab.select3': selectTab(3),
	'tab.select4': selectTab(4),
	'tab.select5': selectTab(5),
	'tab.select6': selectTab(6),
	'tab.select7': selectTab(7),
	'tab.select8': selectTab(8),
	'tab.select9': selectTab(9),
}

export function runCommand(id: CommandId, ctx: CommandContext): CommandOutcome {
	const handler = HANDLERS[id]
	if (!handler) return { status: 'not-implemented', label: getCommand(id).label }
	return { status: handler(ctx) }
}
