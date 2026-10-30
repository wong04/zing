import type { BrowserWindow, WebContents } from 'electron'
import { getCommand, type CommandId } from '@shared/commands'
import type { CommandOutcome } from '@shared/ipc'
import { NEW_TAB_URL, zoomInFactor, zoomOutFactor, type TabId } from '@shared/model'
import { startPageURL } from '@shared/browser/startPage'
import type { LibraryController } from './library-controller'
import type { PageFeatures } from './page-actions'
import type { ShellController } from './shell'
import type { TabOverview } from './tab-overview'
import type { TabService } from './tab-service'

export interface CommandContext {
	readonly tabs: TabService
	readonly features: PageFeatures
	readonly shell: ShellController
	readonly library: LibraryController
	readonly overview: TabOverview
	readonly window: BrowserWindow
	readonly homepage: () => string
	readonly focusAddress: () => void
	readonly newWindow: () => void
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
	'app.settings': (ctx) => {
		ctx.shell.openSettings()
		return 'done'
	},
	'view.sidebar': (ctx) => {
		ctx.shell.toggleSidebar()
		return 'done'
	},
	'library.addFavourite': (ctx) => ctx.library.addFavourite(),
	'library.addReadingList': (ctx) => ctx.library.addReadingList(),
	'library.bookmarks': (ctx) => ctx.library.togglePanel('bookmarks'),
	'library.readingList': (ctx) => ctx.library.togglePanel('readingList'),
	'view.favouritesBar': (ctx) => ctx.library.toggleFavouritesBar(),
	'tabs.overview': (ctx) => {
		void ctx.overview.toggle()
		return 'done'
	},
	'tab.new': (ctx) => {
		ctx.tabs.open(startPageURL)
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
	'window.new': (ctx) => {
		ctx.newWindow()
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
	'page.find': (ctx) =>
		onActivePage(ctx, () => {
			ctx.features.find()
			return 'done'
		}),
	'page.findNext': (ctx) =>
		onActivePage(ctx, () => {
			ctx.features.findStep('next')
			return 'done'
		}),
	'page.findPrevious': (ctx) =>
		onActivePage(ctx, () => {
			ctx.features.findStep('previous')
			return 'done'
		}),
	'page.reader': (ctx) =>
		onActivePage(ctx, (c) => {
			ctx.features.showReader(c)
			return 'done'
		}),
	'page.textBigger': (ctx) =>
		onActivePage(ctx, (c) => {
			ctx.features.textBigger(c)
			return 'done'
		}),
	'page.textSmaller': (ctx) =>
		onActivePage(ctx, (c) => {
			ctx.features.textSmaller(c)
			return 'done'
		}),
	'page.print': (ctx) =>
		onActivePage(ctx, (c) => {
			ctx.features.print(c)
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
