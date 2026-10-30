import { describe, expect, it } from 'vitest'
import { intersects, type Rect } from '@shared/shell/panelLayout'
import { section, Section, tool, UtilityTool } from '@shared/toolbox/panelContent'
import { clampOrigin, PanelNotTearableError, PanelStore, surfaceRegion, UnknownPanelError } from './panels'

const area: Rect = { x: 204, y: 80, width: 1000, height: 700 }
const centre = { x: 700, y: 400 }

function fill(store: PanelStore, count: number): void {
	const tools = [UtilityTool.hash, UtilityTool.jwt, UtilityTool.json, UtilityTool.regex]
	for (const t of tools.slice(0, count)) store.open(tool(t), centre, area)
}

describe('PanelStore', () => {
	it('shows at most three panels and refuses the fourth', () => {
		const store = new PanelStore()
		fill(store, 3)
		expect(store.isFull).toBe(true)
		expect(store.open(tool(UtilityTool.regex), centre, area)).toEqual({ status: 'full' })
		expect(store.all).toHaveLength(3)
	})

	it('opens an already open panel by focusing it, not by adding another', () => {
		const store = new PanelStore()
		fill(store, 2)
		expect(store.open(tool(UtilityTool.hash), centre, area)).toEqual({ status: 'focused' })
		expect(store.all).toHaveLength(2)
		expect(store.state().frontId).toBe(store.all.at(-1)?.id)
	})

	it('minimizing frees a slot, and restoring into a full window is refused', () => {
		const store = new PanelStore()
		fill(store, 3)
		const first = store.all[0]!.id
		store.minimize(first)
		expect(store.isFull).toBe(false)
		expect(store.open(tool(UtilityTool.regex), centre, area)).toEqual({ status: 'opened' })
		expect(store.restore(first, area)).toEqual({ status: 'full' })
		expect(store.all.find((p) => p.id === first)?.minimized).toBe(true)
	})

	it('opening a minimized panel restores it', () => {
		const store = new PanelStore()
		fill(store, 1)
		store.minimize(store.all[0]!.id)
		expect(store.open(tool(UtilityTool.hash), centre, area)).toEqual({ status: 'restored' })
		expect(store.visible).toHaveLength(1)
	})

	it('keeps visible panels apart after a release, moving the others', () => {
		const store = new PanelStore()
		const roomy: Rect = { x: 204, y: 80, width: 1500, height: 900 }
		store.open(tool(UtilityTool.hash), centre, roomy)
		store.open(tool(UtilityTool.jwt), centre, roomy)
		const [a, b] = store.all
		store.beginDrag(b!.id)
		store.endDrag(b!.id, { x: a!.frame.x + 10, y: a!.frame.y + 10 }, roomy)
		const [pa, pb] = store.visible
		expect(intersects(pa!.frame, pb!.frame)).toBe(false)
		expect(store.dragging).toBe(false)
	})

	it('puts the released panel exactly where it was dropped when there is room', () => {
		const store = new PanelStore()
		fill(store, 1)
		const id = store.all[0]!.id
		store.endDrag(id, { x: 300, y: 120 }, area)
		expect(store.all[0]!.frame).toMatchObject({ x: 300, y: 120 })
	})

	it('refuses panels the browser must keep to itself', () => {
		expect(() => new PanelStore().open(section(Section.browser), centre, area)).toThrow(PanelNotTearableError)
	})

	it('throws for an id that names no panel', () => {
		expect(() => new PanelStore().close('nope')).toThrow(UnknownPanelError)
	})

	it('hands out whole-pixel frames', () => {
		const store = new PanelStore()
		store.open(tool(UtilityTool.hash), { x: 700.4, y: 400.7 }, area)
		const f = store.all[0]!.frame
		for (const n of [f.x, f.y, f.width, f.height]) expect(Number.isInteger(n)).toBe(true)
	})
})

describe('clampOrigin', () => {
	it('keeps a grabbable strip of the panel inside the area', () => {
		const size = { width: 400, height: 300 }
		expect(clampOrigin({ x: -9000, y: -9000 }, size, area)).toEqual({ x: area.x + 60 - 400, y: area.y + 6 })
		expect(clampOrigin({ x: 9000, y: 9000 }, size, area)).toEqual({
			x: area.x + area.width - 60,
			y: area.y + area.height - 40,
		})
	})
})

describe('surfaceRegion', () => {
	const content = { width: 1280, height: 780 }
	const base = {
		content,
		page: area,
		gated: false,
		settingsOpen: false,
		sidebarRevealed: false,
		panelFrames: [] as Rect[],
		dragging: false,
	}

	it('is hidden when nothing is shown', () => {
		expect(surfaceRegion(base)).toBeNull()
	})

	it('covers the whole window for the settings sheet and the account gate', () => {
		const whole = { x: 0, y: 0, ...content }
		expect(surfaceRegion({ ...base, settingsOpen: true })).toEqual(whole)
		expect(surfaceRegion({ ...base, gated: true })).toEqual(whole)
	})

	it('hugs a panel plus its shadow rather than the page', () => {
		const panel = { x: 400, y: 200, width: 300, height: 200 }
		expect(surfaceRegion({ ...base, panelFrames: [panel] })).toEqual({ x: 368, y: 168, width: 364, height: 264 })
	})

	it('covers the page while dragging so the pointer stays inside', () => {
		const region = surfaceRegion({ ...base, dragging: true, panelFrames: [{ x: 400, y: 200, width: 300, height: 200 }] })
		expect(region).toEqual(area)
	})

	it('includes the sidebar strip while it is revealed, and stays inside the window', () => {
		const region = surfaceRegion({ ...base, sidebarRevealed: true })
		expect(region).toEqual({ x: 0, y: 0, width: 204, height: 780 })
	})
})
