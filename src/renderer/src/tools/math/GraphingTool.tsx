import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { IconButton, PillButton, PillField, SectionCard, SegmentedControl, Toggle } from '../../components'
import { CloseGlyph } from '../../components/icons'
import { AngleMode, format, makeFunction } from '@shared/math/expression'
import {
	axisLabel,
	defaultViewport,
	extrema,
	graphX,
	graphY,
	intersections,
	panned,
	roots,
	sample,
	screenX,
	screenY,
	tickStep,
	ticks,
	viewportHeight,
	viewportWidth,
	zoomed,
	type Point,
	type Size,
	type Viewport,
} from '@shared/math/graphEngine'
import { plotProblem } from './plotProblem'
import { CopyButton, ToolPage } from '../parts'
import { angleOptions } from './angleOptions'
import './math.css'

/** Distinct hues legible on both the light and dark surfaces, in the order Desmos cycles its palette (Krackn `plotColors`). */
const plotColors = ['#c7543d', '#3d73b8', '#4f8f54', '#99549e', '#d99929', '#4a949c'] as const

const ZOOM_IN = 0.8
const ZOOM_OUT = 1.25
const WHEEL_ZOOM_PER_PIXEL = 1.0015
const HIT_RADIUS = 14
const DRAG_THRESHOLD = 3
const MARKER_LIMITS = { roots: 40, extrema: 20, intersections: 40 }

interface Plot {
	readonly id: number
	readonly text: string
	readonly colorIndex: number
	readonly visible: boolean
}

type MarkerKind = 'intersection' | 'root' | 'extremum'

interface Marker extends Point {
	readonly kind: MarkerKind
	readonly colorIndex: number
}

const markerLabel: Record<MarkerKind, string> = {
	intersection: 'Intersection',
	root: 'X-intercept',
	extremum: 'Turning point',
}

const colourOf = (index: number): string => plotColors[index % plotColors.length] ?? plotColors[0]

/** The plots that are shown and valid: what the canvas draws and what the markers are found on. */
function drawable(plots: readonly Plot[], angle: AngleMode): Plot[] {
	return plots.filter((plot) => plot.visible && plot.text.trim() !== '' && plotProblem(plot.text, angle) === null)
}

/** Roots, turning points and every pairwise intersection of the visible curves: the dots Desmos shows. */
function findMarkers(plots: readonly Plot[], viewport: Viewport, angle: AngleMode): Marker[] {
	const active = drawable(plots, angle).map((plot) => ({ plot, f: makeFunction(plot.text, angle) }))
	const markers: Marker[] = []
	for (const { plot, f } of active) {
		for (const x of roots(f, viewport).slice(0, MARKER_LIMITS.roots)) markers.push({ x, y: 0, kind: 'root', colorIndex: plot.colorIndex })
		for (const p of extrema(f, viewport).slice(0, MARKER_LIMITS.extrema)) markers.push({ x: p.x, y: p.y, kind: 'extremum', colorIndex: plot.colorIndex })
	}
	active.forEach((a, i) => {
		for (const b of active.slice(i + 1)) {
			for (const p of intersections(a.f, b.f, viewport).slice(0, MARKER_LIMITS.intersections)) {
				markers.push({ x: p.x, y: p.y, kind: 'intersection', colorIndex: a.plot.colorIndex })
			}
		}
	})
	return markers
}

interface Palette {
	readonly surface: string
	readonly outline: string
	readonly muted: string
}

function readPalette(canvas: HTMLCanvasElement): Palette {
	const style = getComputedStyle(canvas)
	return {
		surface: style.getPropertyValue('--surface').trim(),
		outline: style.getPropertyValue('--outline').trim(),
		muted: style.getPropertyValue('--muted').trim(),
	}
}

/** `colour` at `alpha`, for canvas strokes that are a tint of a theme token. */
function faded(colour: string, alpha: number): string {
	return `color-mix(in srgb, ${colour} ${Math.round(alpha * 100)}%, transparent)`
}

interface Scene {
	readonly plots: readonly Plot[]
	readonly viewport: Viewport
	readonly angle: AngleMode
	readonly markers: readonly Marker[]
	readonly selected: Marker | null
	readonly hover: Point | null
}

function line(ctx: CanvasRenderingContext2D, x0: number, y0: number, x1: number, y1: number): void {
	ctx.beginPath()
	ctx.moveTo(x0, y0)
	ctx.lineTo(x1, y1)
	ctx.stroke()
}

function drawGrid(ctx: CanvasRenderingContext2D, viewport: Viewport, size: Size, palette: Palette): void {
	const xStep = tickStep(viewportWidth(viewport), Math.floor(size.width / 80))
	const yStep = tickStep(viewportHeight(viewport), Math.floor(size.height / 60))

	// A fine sub-grid at a fifth of the major spacing, as Desmos draws.
	ctx.lineWidth = 1
	for (const [step, alpha] of [[xStep / 5, 0.07], [xStep, 0.16]] as const) {
		ctx.strokeStyle = faded(palette.outline, alpha)
		for (const x of ticks(viewport.xMin, viewport.xMax, step)) {
			const px = screenX(x, viewport, size)
			line(ctx, px, 0, px, size.height)
		}
	}
	for (const [step, alpha] of [[yStep / 5, 0.07], [yStep, 0.16]] as const) {
		ctx.strokeStyle = faded(palette.outline, alpha)
		for (const y of ticks(viewport.yMin, viewport.yMax, step)) {
			const py = screenY(y, viewport, size)
			line(ctx, 0, py, size.width, py)
		}
	}

	const originX = screenX(0, viewport, size)
	const originY = screenY(0, viewport, size)
	ctx.strokeStyle = faded(palette.outline, 0.6)
	ctx.lineWidth = 1.5
	if (originX >= 0 && originX <= size.width) line(ctx, originX, 0, originX, size.height)
	if (originY >= 0 && originY <= size.height) line(ctx, 0, originY, size.width, originY)

	ctx.fillStyle = palette.muted
	ctx.font = '9px ui-monospace, Consolas, monospace'
	ctx.textBaseline = 'top'
	ctx.textAlign = 'center'
	const labelY = Math.min(Math.max(originY, 10), size.height - 6)
	for (const x of ticks(viewport.xMin, viewport.xMax, xStep)) {
		if (Math.abs(x) <= xStep / 1000) continue
		ctx.fillText(axisLabel(x, xStep), screenX(x, viewport, size), labelY + 3)
	}
	ctx.textAlign = 'right'
	ctx.textBaseline = 'middle'
	const labelX = Math.min(Math.max(originX, 6), size.width - 20)
	for (const y of ticks(viewport.yMin, viewport.yMax, yStep)) {
		if (Math.abs(y) <= yStep / 1000) continue
		ctx.fillText(axisLabel(y, yStep), labelX - 5, screenY(y, viewport, size))
	}
}

function drawCurves(ctx: CanvasRenderingContext2D, scene: Scene, size: Size): void {
	ctx.lineWidth = 2.2
	ctx.lineCap = 'round'
	ctx.lineJoin = 'round'
	for (const plot of drawable(scene.plots, scene.angle)) {
		ctx.strokeStyle = colourOf(plot.colorIndex)
		const f = makeFunction(plot.text, scene.angle)
		for (const segment of sample(f, scene.viewport, size, Math.floor(size.width))) {
			ctx.beginPath()
			segment.forEach((p, i) => (i === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y)))
			ctx.stroke()
		}
	}
}

function drawMarkers(ctx: CanvasRenderingContext2D, scene: Scene, size: Size, palette: Palette): void {
	for (const marker of scene.markers) {
		const px = screenX(marker.x, scene.viewport, size)
		const py = screenY(marker.y, scene.viewport, size)
		if (px < -10 || px > size.width + 10 || py < -10 || py > size.height + 10) continue
		const isSelected = scene.selected !== null && Math.abs(scene.selected.x - marker.x) < 1e-9 && Math.abs(scene.selected.y - marker.y) < 1e-9
		const colour = colourOf(marker.colorIndex)
		ctx.beginPath()
		ctx.arc(px, py, isSelected ? 5.5 : 3.8, 0, Math.PI * 2)
		// Filled when selected, hollow otherwise: Desmos's convention.
		ctx.fillStyle = isSelected ? colour : palette.surface
		ctx.fill()
		ctx.lineWidth = isSelected ? 2 : 1.8
		ctx.strokeStyle = isSelected ? palette.surface : colour
		ctx.stroke()
	}
}

function drawCrosshair(ctx: CanvasRenderingContext2D, scene: Scene, size: Size, palette: Palette): void {
	if (!scene.hover) return
	const px = screenX(scene.hover.x, scene.viewport, size)
	const py = screenY(scene.hover.y, scene.viewport, size)
	ctx.setLineDash([3, 3])
	ctx.lineWidth = 1
	ctx.strokeStyle = faded(palette.outline, 0.3)
	line(ctx, px, 0, px, size.height)
	line(ctx, 0, py, size.width, py)
	ctx.setLineDash([])
}

export class CanvasUnavailableError extends Error {
	override name = 'CanvasUnavailableError'
	constructor() {
		super('The graph canvas has no 2D context')
	}
}

function draw(canvas: HTMLCanvasElement, scene: Scene, size: Size): void {
	const ctx = canvas.getContext('2d')
	if (!ctx) throw new CanvasUnavailableError()
	const scale = window.devicePixelRatio
	ctx.setTransform(scale, 0, 0, scale, 0, 0)
	const palette = readPalette(canvas)
	ctx.fillStyle = palette.surface
	ctx.fillRect(0, 0, size.width, size.height)
	drawGrid(ctx, scene.viewport, size, palette)
	drawCurves(ctx, scene, size)
	drawMarkers(ctx, scene, size, palette)
	drawCrosshair(ctx, scene, size, palette)
}

function describeViewport(v: Viewport): string {
	const round = (n: number): string => format(Number(n.toPrecision(4)))
	return `x ${round(v.xMin)} to ${round(v.xMax)}, y ${round(v.yMin)} to ${round(v.yMax)}`
}

/** Bumps when the theme repaints, so the canvas (which cannot use CSS variables itself) is drawn again in the new colours. */
function useThemeVersion(): number {
	const [version, setVersion] = useState(0)
	useEffect(() => {
		const bump = (): void => setVersion((n) => n + 1)
		const scheme = window.matchMedia('(prefers-color-scheme: dark)')
		scheme.addEventListener('change', bump)
		const watcher = new MutationObserver(bump)
		watcher.observe(document.documentElement, { attributes: true })
		return () => {
			scheme.removeEventListener('change', bump)
			watcher.disconnect()
		}
	}, [])
	return version
}

interface Drag {
	readonly start: Point
	readonly origin: Viewport
	moved: boolean
}

/** Graphing calculator laid out like Desmos (Krackn `GraphingCalculatorTool`): expressions down the left, the plot beside them. */
export function GraphingTool() {
	const [plots, setPlots] = useState<readonly Plot[]>([
		{ id: 1, text: 'sin(x)', colorIndex: 0, visible: true },
		{ id: 2, text: 'x/3', colorIndex: 1, visible: true },
	])
	const nextId = useRef(3)
	const [viewport, setViewport] = useState<Viewport>(defaultViewport)
	const [angle, setAngle] = useState<AngleMode>(AngleMode.radians)
	const [showPoints, setShowPoints] = useState(true)
	const [selected, setSelected] = useState<Marker | null>(null)
	const [hover, setHover] = useState<Point | null>(null)
	const [size, setSize] = useState<Size>({ width: 0, height: 0 })
	const canvas = useRef<HTMLCanvasElement>(null)
	const frame = useRef<HTMLDivElement>(null)
	const drag = useRef<Drag | null>(null)
	const theme = useThemeVersion()

	const markers = useMemo(() => (showPoints ? findMarkers(plots, viewport, angle) : []), [plots, viewport, angle, showPoints])

	useEffect(() => {
		const el = frame.current
		if (!el) return
		const measure = (): void => setSize({ width: el.clientWidth, height: el.clientHeight })
		measure()
		const observer = new ResizeObserver(measure)
		observer.observe(el)
		return () => observer.disconnect()
	}, [])

	useEffect(() => {
		const el = canvas.current
		if (!el || size.width === 0 || size.height === 0) return
		el.width = Math.round(size.width * window.devicePixelRatio)
		el.height = Math.round(size.height * window.devicePixelRatio)
		draw(el, { plots, viewport, angle, markers, selected, hover }, size)
	}, [plots, viewport, angle, markers, selected, hover, size, theme])

	// A wheel listener must be non-passive to stop the page scrolling under the plot, which React's onWheel is not.
	useEffect(() => {
		const el = canvas.current
		if (!el) return
		const onWheel = (e: WheelEvent): void => {
			e.preventDefault()
			if (size.width === 0 || size.height === 0) return
			const box = el.getBoundingClientRect()
			const cursor = { x: e.clientX - box.left, y: e.clientY - box.top }
			const factor = Math.pow(WHEEL_ZOOM_PER_PIXEL, e.deltaY)
			setViewport((v) => zoomed(v, factor, { x: graphX(cursor.x, v, size), y: graphY(cursor.y, v, size) }))
		}
		el.addEventListener('wheel', onWheel, { passive: false })
		return () => el.removeEventListener('wheel', onWheel)
	}, [size])

	const local = (e: ReactPointerEvent): Point => {
		const box = e.currentTarget.getBoundingClientRect()
		return { x: e.clientX - box.left, y: e.clientY - box.top }
	}

	const selectNearest = (at: Point): void => {
		if (size.width === 0 || size.height === 0) return
		let best: { marker: Marker; distance: number } | null = null
		for (const marker of markers) {
			const distance = Math.hypot(screenX(marker.x, viewport, size) - at.x, screenY(marker.y, viewport, size) - at.y)
			if (distance < HIT_RADIUS && (!best || distance < best.distance)) best = { marker, distance }
		}
		setSelected(best?.marker ?? null)
	}

	const onPointerDown = (e: ReactPointerEvent<HTMLCanvasElement>): void => {
		if (size.width === 0 || size.height === 0) return
		e.currentTarget.setPointerCapture(e.pointerId)
		drag.current = { start: local(e), origin: viewport, moved: false }
	}
	const onPointerMove = (e: ReactPointerEvent<HTMLCanvasElement>): void => {
		if (size.width === 0 || size.height === 0) return
		const at = local(e)
		const d = drag.current
		if (!d) {
			setHover({ x: graphX(at.x, viewport, size), y: graphY(at.y, viewport, size) })
			return
		}
		const dx = at.x - d.start.x
		const dy = at.y - d.start.y
		if (!d.moved && Math.hypot(dx, dy) < DRAG_THRESHOLD) return
		d.moved = true
		setHover(null)
		setViewport(panned(d.origin, (-dx / size.width) * viewportWidth(d.origin), (dy / size.height) * viewportHeight(d.origin)))
	}
	const onPointerUp = (e: ReactPointerEvent<HTMLCanvasElement>): void => {
		const d = drag.current
		drag.current = null
		if (!d || d.moved) return
		selectNearest(local(e))
	}

	const zoomBy = (factor: number): void => {
		const centre = { x: (viewport.xMin + viewport.xMax) / 2, y: (viewport.yMin + viewport.yMax) / 2 }
		setViewport(zoomed(viewport, factor, centre))
	}
	const reset = (): void => {
		setViewport(defaultViewport)
		setSelected(null)
	}

	const update = (id: number, change: Partial<Plot>): void => setPlots((all) => all.map((p) => (p.id === id ? { ...p, ...change } : p)))
	const add = (): void => {
		const id = nextId.current++
		setPlots((all) => [...all, { id, text: '', colorIndex: all.length % plotColors.length, visible: true }])
	}
	const remove = (id: number): void => setPlots((all) => all.filter((p) => p.id !== id))

	return (
		<ToolPage subtitle="Scroll to zoom, drag to pan. Points of interest are marked automatically.">
			<div className="graph-split">
				<div className="graph-expressions">
					<div className="graph-expressions-head">
						<span>Expressions</span>
						<IconButton aria-label="Add expression" size={22} onClick={add}>
							+
						</IconButton>
					</div>
					<div className="graph-expression-list">
						{plots.map((plot, index) => {
							const problem = plotProblem(plot.text, angle)
							const colour = colourOf(plot.colorIndex)
							return (
								<div key={plot.id} className="graph-expression">
									<div className="graph-gutter">
										<span className="math-mono graph-index">{index + 1}</span>
										<button
											type="button"
											className="graph-swatch"
											aria-label={`${plot.visible ? 'Hide' : 'Show'} expression ${index + 1}`}
											aria-pressed={plot.visible}
											style={{ borderColor: colour, background: plot.visible ? colour : 'transparent' }}
											onClick={() => update(plot.id, { visible: !plot.visible })}
										/>
									</div>
									<div className="graph-expression-body">
										<PillField
											aria-label={`Expression ${index + 1}`}
											placeholder="y = …"
											spellCheck={false}
											value={plot.text}
											onChange={(text) => update(plot.id, { text })}
										/>
										{problem ? <span className="graph-problem">{problem}</span> : null}
									</div>
									{plots.length > 1 ? (
										<IconButton aria-label={`Remove expression ${index + 1}`} size={22} onClick={() => remove(plot.id)}>
											<CloseGlyph size={11} />
										</IconButton>
									) : null}
								</div>
							)
						})}
					</div>
					<div className="graph-footer">
						<div className="math-row graph-toggle">
							<span>Show key points</span>
							<Toggle checked={showPoints} onChange={setShowPoints} aria-label="Show key points" />
						</div>
						<div className="math-row">
							<SegmentedControl options={angleOptions} value={angle} onChange={setAngle} aria-label="Angle unit" width={120} />
							<PillButton variant="ghost" size="small" onClick={reset}>
								Reset
							</PillButton>
						</div>
					</div>
				</div>
				<div className="graph-plot" ref={frame}>
					<canvas
						ref={canvas}
						className="graph-canvas"
						data-testid="graph-canvas"
						style={{ width: size.width, height: size.height }}
						role="img"
						aria-label="Graph"
						onPointerDown={onPointerDown}
						onPointerMove={onPointerMove}
						onPointerUp={onPointerUp}
						onPointerLeave={() => setHover(null)}
					/>
					<div className="graph-zoom">
						<IconButton aria-label="Zoom in" size={26} onClick={() => zoomBy(ZOOM_IN)}>
							+
						</IconButton>
						<IconButton aria-label="Zoom out" size={26} onClick={() => zoomBy(ZOOM_OUT)}>
							−
						</IconButton>
					</div>
					<div className="graph-readout math-mono" data-testid="graph-viewport">
						{describeViewport(viewport)}
					</div>
					{hover ? (
						<div className="graph-hover math-mono">
							({format(hover.x)}, {format(hover.y)})
						</div>
					) : null}
				</div>
			</div>

			{selected ? (
				<SectionCard title={markerLabel[selected.kind]} compact>
					<div className="math-row">
						<span className="math-mono graph-selected">
							({format(selected.x)}, {format(selected.y)})
						</span>
						<CopyButton value={`(${format(selected.x)}, ${format(selected.y)})`} label="point" />
						<PillButton variant="ghost" size="small" onClick={() => setSelected(null)}>
							Dismiss
						</PillButton>
					</div>
				</SectionCard>
			) : null}
		</ToolPage>
	)
}
