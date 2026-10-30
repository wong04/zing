// Port of Krackn App/FloatingPanels.swift (`PanelStore`) and the geometry
// around it. No Electron here: the store works in window content coordinates
// and is driven by `ShellController`, so every rule can be tested directly.
import { resolveAfterDrop, type Rect } from '@shared/shell/panelLayout'
import {
	MAX_VISIBLE_PANELS,
	SIDEBAR_COLUMN_WIDTH,
	type OpenPanelOutcome,
	type Point,
	type ToolPanelsState,
} from '@shared/shell/shellState'
import { isTearable, panelId, panelTitle, preferredSize, type PanelContent } from '@shared/toolbox/panelContent'

export class PanelNotTearableError extends Error {
	constructor(readonly id: string) {
		super(`${id} can't be opened in a panel`)
		this.name = 'PanelNotTearableError'
	}
}

export class UnknownPanelError extends Error {
	constructor(readonly id: string) {
		super(`No panel with id ${id}`)
		this.name = 'UnknownPanelError'
	}
}

export interface PanelRecord {
	readonly id: string
	readonly content: PanelContent
	readonly frame: Rect
	readonly minimized: boolean
}

/** How much of a panel must stay inside the area so it can always be grabbed again. */
const GRAB_MARGIN = 60
const TOP_MARGIN = 6
const BOTTOM_MARGIN = 40
/** A panel never fills the area edge to edge. */
const AREA_PADDING = 80
const MIN_WIDTH = 320
const MIN_HEIGHT = 300
/** Opening centres the panel under the cursor, a little below its title bar's middle. */
const TITLE_OFFSET = 18

function whole(r: Rect): Rect {
	return { x: Math.round(r.x), y: Math.round(r.y), width: Math.round(r.width), height: Math.round(r.height) }
}

/** Keeps `size` within `area` by the grab margin, so a dragged panel can hang off the edge but never vanish. */
export function clampOrigin(point: Point, size: { width: number; height: number }, area: Rect): Point {
	const maxX = Math.max(area.x + area.width - GRAB_MARGIN, area.x)
	const maxY = Math.max(area.y + area.height - BOTTOM_MARGIN, area.y)
	return {
		x: Math.min(Math.max(point.x, area.x + GRAB_MARGIN - size.width), maxX),
		y: Math.min(Math.max(point.y, area.y + TOP_MARGIN), maxY),
	}
}

export class PanelStore {
	#panels: PanelRecord[] = []
	#frontId: string | undefined
	#dragging = false
	#next = 1

	get visible(): readonly PanelRecord[] {
		return this.#panels.filter((p) => !p.minimized)
	}

	get isFull(): boolean {
		return this.visible.length >= MAX_VISIBLE_PANELS
	}

	get dragging(): boolean {
		return this.#dragging
	}

	get all(): readonly PanelRecord[] {
		return this.#panels
	}

	state(): ToolPanelsState {
		return {
			panels: this.#panels.map((p) => ({
				id: p.id,
				content: panelId(p.content),
				title: panelTitle(p.content),
				frame: p.frame,
				minimized: p.minimized,
			})),
			frontId: this.#frontId,
			dragging: this.#dragging,
		}
	}

	open(content: PanelContent, at: Point, area: Rect): OpenPanelOutcome {
		if (content.kind === 'section' && !isTearable(content.section)) throw new PanelNotTearableError(panelId(content))
		const existing = this.#panels.find((p) => panelId(p.content) === panelId(content))
		if (existing?.minimized) return this.restore(existing.id, area)
		if (existing) {
			this.bringToFront(existing.id)
			return { status: 'focused' }
		}
		if (this.isFull) return { status: 'full' }

		const preferred = preferredSize(content)
		const size = {
			width: Math.min(preferred.width, Math.max(area.width - AREA_PADDING, MIN_WIDTH)),
			height: Math.min(preferred.height, Math.max(area.height - AREA_PADDING, MIN_HEIGHT)),
		}
		const origin = clampOrigin({ x: at.x - size.width / 2, y: at.y - TITLE_OFFSET }, size, area)
		const id = `panel-${this.#next++}`
		this.#panels.push({ id, content, frame: whole({ ...origin, ...size }), minimized: false })
		this.#frontId = id
		this.#settle(id, area)
		return { status: 'opened' }
	}

	close(id: string): void {
		this.#record(id)
		this.#panels = this.#panels.filter((p) => p.id !== id)
		if (this.#frontId === id) this.#frontId = this.visible.at(-1)?.id
	}

	minimize(id: string): void {
		this.#replace(id, (p) => ({ ...p, minimized: true }))
		if (this.#frontId === id) this.#frontId = this.visible.at(-1)?.id
	}

	restore(id: string, area: Rect): OpenPanelOutcome {
		const panel = this.#record(id)
		if (!panel.minimized) {
			this.bringToFront(id)
			return { status: 'focused' }
		}
		if (this.isFull) return { status: 'full' }
		this.#replace(id, (p) => ({ ...p, frame: this.#clamped(p.frame, area), minimized: false }))
		this.bringToFront(id)
		this.#settle(id, area)
		return { status: 'restored' }
	}

	bringToFront(id: string): void {
		const panel = this.#record(id)
		this.#panels = [...this.#panels.filter((p) => p.id !== id), panel]
		this.#frontId = id
	}

	beginDrag(id: string): void {
		this.bringToFront(id)
		this.#dragging = true
	}

	/**
	 * Other panels make room only now, on release. Resolving every frame mid-drag
	 * was rejected in Krackn as distracting; the dragged panel is local view
	 * state until this point.
	 */
	endDrag(id: string, origin: Point, area: Rect): void {
		this.#dragging = false
		this.#replace(id, (p) => ({ ...p, frame: this.#clamped({ ...p.frame, ...origin }, area) }))
		this.#settle(id, area)
	}

	/** After the window resizes: nothing may be left beyond reach. */
	clampAll(area: Rect): void {
		this.#panels = this.#panels.map((p) => ({ ...p, frame: this.#clamped(p.frame, area) }))
	}

	#clamped(frame: Rect, area: Rect): Rect {
		return whole({ ...frame, ...clampOrigin(frame, frame, area) })
	}

	#settle(id: string, area: Rect): void {
		const shown = this.visible
		const fixed = shown.findIndex((p) => p.id === id)
		if (fixed === -1) return
		const resolved = resolveAfterDrop(
			fixed,
			shown.map((p) => p.frame),
			area,
		)
		const frames = new Map(shown.map((p, i) => [p.id, whole(resolved[i] as Rect)]))
		this.#panels = this.#panels.map((p) => ({ ...p, frame: frames.get(p.id) ?? p.frame }))
	}

	#record(id: string): PanelRecord {
		const panel = this.#panels.find((p) => p.id === id)
		if (!panel) throw new UnknownPanelError(id)
		return panel
	}

	#replace(id: string, change: (panel: PanelRecord) => PanelRecord): void {
		this.#record(id)
		this.#panels = this.#panels.map((p) => (p.id === id ? change(p) : p))
	}
}

export interface SurfaceInput {
	/** The window's content size. */
	readonly content: { readonly width: number; readonly height: number }
	/** Where the page view sits (below the chrome, right of a pinned sidebar). */
	readonly page: Rect
	readonly gated: boolean
	readonly settingsOpen: boolean
	readonly sidebarRevealed: boolean
	readonly panelFrames: readonly Rect[]
	readonly dragging: boolean
}

/** Room for a panel's soft shadow, which would otherwise be cut off at the view's edge. */
const SHADOW_ROOM = 32

function union(rects: readonly Rect[]): Rect {
	const left = Math.min(...rects.map((r) => r.x))
	const top = Math.min(...rects.map((r) => r.y))
	const right = Math.max(...rects.map((r) => r.x + r.width))
	const bottom = Math.max(...rects.map((r) => r.y + r.height))
	return { x: left, y: top, width: right - left, height: bottom - top }
}

function clippedToContent(r: Rect, content: SurfaceInput['content']): Rect | null {
	const x = Math.max(0, Math.floor(r.x))
	const y = Math.max(0, Math.floor(r.y))
	const right = Math.min(content.width, Math.ceil(r.x + r.width))
	const bottom = Math.min(content.height, Math.ceil(r.y + r.height))
	if (right <= x || bottom <= y) return null
	return { x, y, width: right - x, height: bottom - y }
}

/**
 * The surface view is sized to what it shows and hidden when it shows nothing
 * (null), so it never takes clicks meant for the page: Krackn's "SwiftUI over
 * the page" gotcha. It covers the whole window for the gate and the settings
 * sheet, and the whole page while a panel is dragged (so the pointer stays
 * inside it), and otherwise hugs the revealed sidebar and the open panels.
 */
export function surfaceRegion(input: SurfaceInput): Rect | null {
	const { content } = input
	if (input.gated || input.settingsOpen) return { x: 0, y: 0, width: content.width, height: content.height }
	const parts: Rect[] = []
	if (input.sidebarRevealed) parts.push({ x: 0, y: 0, width: SIDEBAR_COLUMN_WIDTH, height: content.height })
	if (input.dragging) parts.push(input.page)
	else {
		for (const f of input.panelFrames) {
			parts.push({
				x: f.x - SHADOW_ROOM,
				y: f.y - SHADOW_ROOM,
				width: f.width + 2 * SHADOW_ROOM,
				height: f.height + 2 * SHADOW_ROOM,
			})
		}
	}
	if (parts.length === 0) return null
	return clippedToContent(union(parts), content)
}
