import { WebContentsView, type BrowserWindow, type WebContents } from 'electron'
import type { Stack } from '@shared/browser/tabOrganizer'
import { CHROME_LAYOUT, type DragPresenceSource, type StackDropdownState, type TabList } from '@shared/ipc'

export interface StackMembers {
	readonly name: string
	readonly tabs: StackDropdownState['tabs']
	readonly activeId: StackDropdownState['activeId']
}

/** The live pages of a stack, or undefined once it holds fewer than two. */
export function stackMembersOf(list: TabList, stacks: readonly Stack[], key: string): StackMembers | undefined {
	const stack = stacks.find((s) => s.key === key && s.ids.length > 1)
	if (!stack) return undefined
	const tabs = stack.ids.flatMap((id) => list.tabs.filter((t) => t.id === id))
	return { name: stack.name, tabs, activeId: list.activeId }
}

export interface StackDropdownOptions {
	readonly window: BrowserWindow
	readonly preload: string
	readonly load: (contents: WebContents) => Promise<void>
	readonly onContentsCreated: (contents: WebContents) => void
	/** The pages of the stack with this key, or undefined once it has fewer than two. */
	readonly members: (key: string) => StackMembers | undefined
	/** The open key changed, so the strip should redraw its pressed stack chip. */
	readonly onChange: () => void
}

/** A file held over the stack opens it; leaving both chip and list closes it again after this long. */
const DRAG_CLOSE_MS = 300

/**
 * The tab-stack dropdown. Like the notice layer it is a transparent view above
 * the pages, because a page view paints over the chrome renderer and a dropdown
 * there would never get a click. While open it covers everything under the tab
 * strip, so a click anywhere else lands on its backdrop and closes it, and the
 * page beneath never sees the click (Krackn's shield for the same trap).
 */
export class StackDropdown {
	#view: WebContentsView | undefined
	#ready: Promise<WebContentsView> | undefined
	#key: string | undefined
	#anchorX = 0
	#payload: StackDropdownState | null = null
	#openedByDrag = false
	#over: Record<DragPresenceSource, boolean> = { chip: false, panel: false }
	#dragCloseTimer: ReturnType<typeof setTimeout> | undefined

	constructor(private readonly options: StackDropdownOptions) {}

	get openKey(): string | undefined {
		return this.#key
	}

	async toggle(key: string, anchorX: number): Promise<void> {
		if (this.#key === key) {
			this.close()
			return
		}
		await this.#open(key, anchorX, false)
	}

	/** A dragged file is held over the stack chip. */
	async openForDrag(key: string, anchorX: number): Promise<void> {
		if (this.#key === key) return
		await this.#open(key, anchorX, true)
	}

	close(): void {
		clearTimeout(this.#dragCloseTimer)
		this.#openedByDrag = false
		this.#over = { chip: false, panel: false }
		if (this.#key === undefined) return
		this.#key = undefined
		this.#payload = null
		if (this.#view && !this.#view.webContents.isDestroyed()) {
			this.#view.setVisible(false)
			this.#view.webContents.send('stack:dropdown', null)
		}
		this.options.onChange()
	}

	/** Re-sends the pages after any tab change, or closes when the stack no longer has two. */
	refresh(): void {
		if (this.#key === undefined || !this.#view) return
		const members = this.options.members(this.#key)
		if (!members) {
			this.close()
			return
		}
		this.#send(this.#key, members)
	}

	/** For a dropdown renderer that finished loading after the first push. */
	current(): StackDropdownState | null {
		return this.#payload
	}

	dragPresence(source: DragPresenceSource, inside: boolean): void {
		this.#over = { ...this.#over, [source]: inside }
		clearTimeout(this.#dragCloseTimer)
		if (!this.#openedByDrag || this.#over.chip || this.#over.panel) return
		this.#dragCloseTimer = setTimeout(() => this.close(), DRAG_CLOSE_MS)
	}

	/** Re-adding a child moves it to the top; called after a page view is attached. */
	raise(): void {
		if (this.#view && this.#key !== undefined) this.options.window.contentView.addChildView(this.#view)
	}

	layout(): void {
		if (this.#view && this.#key !== undefined) this.#view.setBounds(this.#bounds())
	}

	destroy(): void {
		clearTimeout(this.#dragCloseTimer)
		if (this.#view && !this.#view.webContents.isDestroyed()) this.#view.webContents.close()
	}

	async #open(key: string, anchorX: number, byDrag: boolean): Promise<void> {
		const members = this.options.members(key)
		if (!members) return
		const view = await this.#ensure()
		this.#key = key
		this.#anchorX = anchorX
		this.#openedByDrag = byDrag
		this.#over = { chip: byDrag, panel: false }
		view.setBounds(this.#bounds())
		this.#send(key, members)
		this.options.window.contentView.addChildView(view)
		view.setVisible(true)
		// Keys (arrows, Enter, Escape) go to the list, not the page underneath.
		view.webContents.focus()
		this.options.onChange()
	}

	#send(key: string, members: StackMembers): void {
		this.#payload = { key, anchorX: this.#anchorX, ...members }
		this.#view?.webContents.send('stack:dropdown', this.#payload)
	}

	#bounds(): Electron.Rectangle {
		const { width, height } = this.options.window.getContentBounds()
		const top = CHROME_LAYOUT.tabStripHeight
		return { x: 0, y: top, width, height: Math.max(0, height - top) }
	}

	/** Created on first use, so a window that never opens a stack pays nothing. */
	#ensure(): Promise<WebContentsView> {
		this.#ready ??= this.#create()
		return this.#ready
	}

	async #create(): Promise<WebContentsView> {
		const view = new WebContentsView({
			webPreferences: {
				preload: this.options.preload,
				sandbox: true,
				contextIsolation: true,
				nodeIntegration: false,
				partition: 'zing-chrome',
			},
		})
		view.setBackgroundColor('#00000000')
		view.setVisible(false)
		this.options.window.contentView.addChildView(view)
		this.options.onContentsCreated(view.webContents)
		this.#view = view
		await this.options.load(view.webContents)
		return view
	}
}
