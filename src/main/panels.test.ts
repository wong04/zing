// The data-shaped cases live in panels.vectors.json, which the C# port (ToolPanelsTests.cs) reads too.
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { intersects, type Rect } from '@shared/shell/panelLayout'
import { panelContentFromId } from '@shared/toolbox/panelContent'
import { clampOrigin, PanelNotTearableError, PanelStore, surfaceRegion, UnknownPanelError, type SurfaceInput } from './panels'

type RectTuple = readonly [number, number, number, number]
type PointTuple = readonly [number, number]

interface Step {
	readonly op: 'open' | 'minimize' | 'close' | 'restore' | 'focus' | 'beginDrag' | 'endDrag'
	readonly content?: string
	readonly at?: PointTuple
	readonly as?: string
	readonly want?: string
	readonly throws?: 'notTearable' | 'unknownPanel'
	readonly panel?: string
	readonly id?: string
	readonly origin?: PointTuple
	readonly originFromFrame?: { panel: string; dx: number; dy: number }
}

interface Checks {
	readonly all?: number
	readonly visible?: number
	readonly isFull?: boolean
	readonly dragging?: boolean
	readonly front?: string
	readonly minimized?: Record<string, boolean>
	readonly frame?: { panel: string; x: number; y: number; width?: number; height?: number }
	readonly wholePixels?: string
	readonly disjointVisible?: boolean
}

interface SurfaceCase {
	readonly name: string
	readonly input: {
		content: PointTuple
		page: RectTuple
		gated: boolean
		settingsOpen: boolean
		sidebarRevealed: boolean
		panelFrames: RectTuple[]
		dragging: boolean
	}
	readonly want: RectTuple | null
}

const vectors = JSON.parse(readFileSync(new URL('./panels.vectors.json', import.meta.url), 'utf8')) as {
	scenarios: { name: string; area: RectTuple; steps: Step[]; checks: Checks }[]
	clamp: { name: string; point: PointTuple; size: PointTuple; area: RectTuple; want: PointTuple }[]
	surface: SurfaceCase[]
}

const toRect = (t: RectTuple): Rect => ({ x: t[0], y: t[1], width: t[2], height: t[3] })
const toPoint = (t: PointTuple) => ({ x: t[0], y: t[1] })
const fromRect = (r: Rect | null): RectTuple | null => (r ? [r.x, r.y, r.width, r.height] : null)

function need<T>(value: T | undefined, what: string): T {
	if (value === undefined) throw new TypeError(`vector step has no ${what}`)
	return value
}

describe('PanelStore (shared vectors)', () => {
	it.each(vectors.scenarios.map((s) => [s.name, s] as const))('%s', (_name, s) => {
		const store = new PanelStore()
		const area = toRect(s.area)
		const ids = new Map<string, string>()
		const idOf = (step: Step): string => (step.id ?? ids.get(need(step.panel, 'panel'))) as string
		const frameOf = (name: string): Rect => {
			const found = store.all.find((p) => p.id === ids.get(name))
			if (!found) throw new TypeError(`no panel named ${name}`)
			return found.frame
		}

		for (const step of s.steps) {
			const run = (): unknown => {
				switch (step.op) {
					case 'open': {
						const content = panelContentFromId(need(step.content, 'content'))
						if (!content) throw new TypeError(`bad content ${step.content}`)
						return store.open(content, toPoint(need(step.at, 'at')), area)
					}
					case 'minimize':
						return store.minimize(idOf(step))
					case 'close':
						return store.close(idOf(step))
					case 'restore':
						return store.restore(idOf(step), area)
					case 'focus':
						return store.bringToFront(idOf(step))
					case 'beginDrag':
						return store.beginDrag(idOf(step))
					case 'endDrag': {
						const from = step.originFromFrame
						const origin = from
							? { x: frameOf(from.panel).x + from.dx, y: frameOf(from.panel).y + from.dy }
							: toPoint(need(step.origin, 'origin'))
						return store.endDrag(idOf(step), origin, area)
					}
				}
			}
			if (step.throws) {
				expect(run).toThrow(step.throws === 'notTearable' ? PanelNotTearableError : UnknownPanelError)
				continue
			}
			const outcome = run()
			if (step.want) expect(outcome).toEqual({ status: step.want })
			if (step.as) ids.set(step.as, store.all.at(-1)!.id)
		}

		const c = s.checks
		if (c.all !== undefined) expect(store.all).toHaveLength(c.all)
		if (c.visible !== undefined) expect(store.visible).toHaveLength(c.visible)
		if (c.isFull !== undefined) expect(store.isFull).toBe(c.isFull)
		if (c.dragging !== undefined) expect(store.dragging).toBe(c.dragging)
		if (c.front !== undefined) expect(store.state().frontId).toBe(ids.get(c.front))
		for (const [name, minimized] of Object.entries(c.minimized ?? {})) {
			expect(store.all.find((p) => p.id === ids.get(name))?.minimized).toBe(minimized)
		}
		if (c.frame) {
			const f = frameOf(c.frame.panel)
			expect(f).toMatchObject({ x: c.frame.x, y: c.frame.y })
			if (c.frame.width !== undefined) expect(f).toMatchObject({ width: c.frame.width, height: c.frame.height })
		}
		if (c.wholePixels) {
			const f = frameOf(c.wholePixels)
			for (const n of [f.x, f.y, f.width, f.height]) expect(Number.isInteger(n)).toBe(true)
		}
		if (c.disjointVisible) {
			const shown = store.visible
			for (const [i, a] of shown.entries()) for (const b of shown.slice(i + 1)) expect(intersects(a.frame, b.frame)).toBe(false)
		}
	})
})

describe('clampOrigin (shared vectors)', () => {
	it.each(vectors.clamp.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		const origin = clampOrigin(toPoint(c.point), { width: c.size[0], height: c.size[1] }, toRect(c.area))
		expect([origin.x, origin.y]).toEqual(c.want)
	})
})

describe('surfaceRegion (shared vectors)', () => {
	it.each(vectors.surface.map((c) => [c.name, c] as const))('%s', (_name, c) => {
		const input: SurfaceInput = {
			content: { width: c.input.content[0], height: c.input.content[1] },
			page: toRect(c.input.page),
			gated: c.input.gated,
			settingsOpen: c.input.settingsOpen,
			sidebarRevealed: c.input.sidebarRevealed,
			panelFrames: c.input.panelFrames.map(toRect),
			dragging: c.input.dragging,
		}
		expect(fromRect(surfaceRegion(input))).toEqual(c.want)
	})
})
