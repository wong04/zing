import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { PDF_LIMITS, PdfEditError, type OverlayItem } from '@shared/pdf/pdfCore'
import { zing } from '../../api'
import { InfoBanner, PillButton, PillField, PillSlider } from '../../components'
import { describeFailure } from '../dev/parts'
import { usePanelId } from '../panelId'
import { readGranted } from './pdfHost'
import type { PlacedItem } from './pdfModel'
import type { PagePictureSize, PdfPreview } from './pdfRender'
import { toPicture } from './pictureBytes'
import { SignaturePad } from './SignaturePad'

/** The page is shown this many CSS pixels wide; items are kept in points on the page, so this only decides how big they look here. */
const DISPLAY_WIDTH = 440
const NUDGE = 2
const BIG_NUDGE = 10

/**
 * A placed picture, drawn on a canvas from the bytes by the browser's own decoder (createImageBitmap). The interface's content
 * security policy lets pictures in from itself and from data, not from blob, and a data address would mean base64 of up to 8 MB
 * on this thread; a canvas needs neither. It is drawn no larger than 1024 pixels on a side, which is plenty for a preview.
 */
function PictureView({ item, scale, onFail }: { item: Extract<OverlayItem, { kind: 'image' }>; scale: number; onFail: (reason: string) => void }) {
	const canvas = useRef<HTMLCanvasElement>(null)
	const [failed, setFailed] = useState(false)
	// The parent makes a new function on every render; depending on it would redraw each time.
	const report = useRef(onFail)
	useEffect(() => {
		report.current = onFail
	})
	useEffect(() => {
		let live = true
		createImageBitmap(new Blob([item.bytes as BlobPart])).then(
			(bitmap) => {
				if (!live) {
					bitmap.close()
					return
				}
				const target = canvas.current!
				const shrink = Math.min(1, 1024 / Math.max(bitmap.width, bitmap.height))
				target.width = Math.max(1, Math.round(bitmap.width * shrink))
				target.height = Math.max(1, Math.round(bitmap.height * shrink))
				target.getContext('2d')!.drawImage(bitmap, 0, 0, target.width, target.height)
				bitmap.close()
			},
			(error: unknown) => {
				if (!live) return
				setFailed(true)
				report.current(error instanceof Error ? error.message : 'the browser could not decode it')
			},
		)
		return () => {
			live = false
		}
	}, [item.bytes])
	if (failed) return <span role="img" aria-label="Picture that can't be shown" style={{ display: 'block', width: item.width * scale, height: item.height * scale, border: '1px dashed currentColor', fontSize: 11 }}>Can&apos;t show this picture</span>
	return <canvas ref={canvas} aria-hidden="true" style={{ width: item.width * scale, height: item.height * scale }} />
}

export interface PdfPageEditorProps {
	readonly preview: PdfPreview
	/** From 0, in the file. */
	readonly index: number
	/** Where the page is in the result, from 1. */
	readonly position: number
	readonly turn: number
	readonly items: readonly PlacedItem[]
	readonly onAdd: (item: OverlayItem) => void
	readonly onMove: (id: number, to: { x: number; y: number }) => void
	readonly onResize: (id: number, size: number) => void
	readonly onEditText: (id: number, text: string) => void
	readonly onRemove: (id: number) => void
	readonly onClose: () => void
}

/** One page, large, with its added text, pictures and signatures; each can be dragged, or moved with the arrow keys. */
export function PdfPageEditor({ preview, index, position, turn, items, onAdd, onMove, onResize, onEditText, onRemove, onClose }: PdfPageEditorProps) {
	const panelId = usePanelId()
	const canvas = useRef<HTMLCanvasElement>(null)
	const [size, setSize] = useState<PagePictureSize | null>(null)
	const [selected, setSelected] = useState<number | null>(null)
	const [drag, setDrag] = useState<{ id: number; x: number; y: number } | null>(null)
	const [text, setText] = useState('')
	const [signing, setSigning] = useState(false)
	const [problem, setProblem] = useState<string | null>(null)
	const grab = useRef<{ pointerX: number; pointerY: number; x: number; y: number } | null>(null)

	useEffect(() => {
		let live = true
		preview.pageSize(index).then((s) => live && setSize(s), (e: unknown) => live && setProblem(describeFailure(e)))
		return () => {
			live = false
		}
	}, [preview, index])

	useEffect(() => {
		if (size === null) return
		const run = preview.draw(index, canvas.current!, DISPLAY_WIDTH, 0)
		run.done.catch((e: unknown) => setProblem(describeFailure(e)))
		return run.cancel
	}, [preview, index, size])

	const scale = size === null ? 1 : DISPLAY_WIDTH / size.width
	const height = size === null ? 0 : size.height * scale
	const current = items.find((i) => i.id === selected) ?? null

	const fail = (error: unknown): void => setProblem(describeFailure(error))

	// New things start near the top left, each a little below the one before, so none hides another.
	const startAt = useMemo(() => ({ x: 36 + (items.length % 8) * 12, y: 36 + (items.length % 8) * 12 }), [items.length])

	const addText = (): void => {
		if (text.trim() === '') return
		setProblem(null)
		onAdd({ kind: 'text', text, ...startAt, size: 14 })
		setText('')
	}

	const addPicture = (file: { token: string; name: string }): void => {
		readGranted(panelId, file.token)
			.then(async ({ bytes }) => {
				if (bytes.length > PDF_LIMITS.maxOverlayImageBytes) throw new PdfEditError(`${file.name} is over ${PDF_LIMITS.maxOverlayImageBytes / (1024 * 1024)} MB; use a smaller picture.`)
				const picture = await toPicture(file.name, bytes)
				const bitmap = await createImageBitmap(new Blob([picture.bytes as BlobPart]))
				const shape = { width: bitmap.width, height: bitmap.height }
				bitmap.close()
				placePicture(picture.bytes, picture.format, shape.width, shape.height)
			})
			.catch(fail)
	}

	const placePicture = (bytes: Uint8Array, format: 'png' | 'jpg', pixelWidth: number, pixelHeight: number): void => {
		const width = Math.min(150, (size?.width ?? 600) / 3)
		onAdd({ kind: 'image', bytes, format, ...startAt, width, height: (pixelHeight / pixelWidth) * width })
	}

	const choosePicture = (): void => {
		setProblem(null)
		zing.pickFiles(['picture']).then((files) => {
			const first = files[0]
			if (first !== undefined) addPicture(first)
		}, fail)
	}

	const down = (event: PointerEvent<HTMLElement>, placed: PlacedItem): void => {
		event.currentTarget.setPointerCapture(event.pointerId)
		setSelected(placed.id)
		grab.current = { pointerX: event.clientX, pointerY: event.clientY, x: placed.item.x, y: placed.item.y }
	}
	const move = (event: PointerEvent<HTMLElement>, placed: PlacedItem): void => {
		const start = grab.current
		if (start === null) return
		setDrag({ id: placed.id, x: start.x + (event.clientX - start.pointerX) / scale, y: start.y + (event.clientY - start.pointerY) / scale })
	}
	const up = (): void => {
		grab.current = null
		if (drag !== null) onMove(drag.id, { x: drag.x, y: drag.y })
		setDrag(null)
	}

	const key = (event: KeyboardEvent<HTMLElement>, placed: PlacedItem): void => {
		const step = event.shiftKey ? BIG_NUDGE : NUDGE
		const moves: Record<string, [number, number]> = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }
		const by = moves[event.key]
		if (by !== undefined) {
			event.preventDefault()
			onMove(placed.id, { x: placed.item.x + by[0], y: placed.item.y + by[1] })
			return
		}
		if (event.key === 'Delete' || event.key === 'Backspace') {
			event.preventDefault()
			onRemove(placed.id)
			setSelected(null)
		}
	}

	return (
		<div className="pdf-editor">
			<div className="convert-row">
				<strong>{`Page ${position}`}</strong>
				<PillButton size="small" variant="outlined" onClick={onClose}>
					Done
				</PillButton>
			</div>
			{turn === 0 ? null : <span className="convert-hint">{`This page is turned ${turn} degrees in the result. It is shown upright here; what you place turns with it.`}</span>}
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
			<div className="pdf-editor-tools">
				<PillField aria-label="Text to add" placeholder="Text to add" value={text} onChange={setText} onSubmit={addText} maxLength={200} />
				<PillButton size="small" disabled={text.trim() === ''} onClick={addText}>
					Add text
				</PillButton>
				<PillButton size="small" variant="outlined" onClick={choosePicture}>
					Add picture…
				</PillButton>
				<PillButton size="small" variant="outlined" onClick={() => setSigning(true)}>
					Add signature…
				</PillButton>
			</div>
			<span className="convert-hint">Text uses Helvetica: Western European letters, digits and punctuation. Drag a piece, or select it and use the arrow keys (Shift for bigger steps); Delete removes it.</span>
			<span className="convert-hint">A signature here is a picture of your signature placed on the page. It is not a digital signature: nothing proves who added it, or that the page was not changed afterwards.</span>
			{signing ? (
				<SignaturePad
					onCancel={() => setSigning(false)}
					onError={fail}
					onUse={(png, w, h) => {
						setSigning(false)
						placePicture(png, 'png', w, h)
					}}
				/>
			) : null}
			<div className="pdf-sheet" style={{ width: DISPLAY_WIDTH, height }}>
				<canvas ref={canvas} aria-label={`Page ${position}`} className="pdf-sheet-canvas" style={{ width: DISPLAY_WIDTH, height }} />
				{items.map((placed) => {
					const live = drag?.id === placed.id ? drag : placed.item
					const style = { left: live.x * scale, top: live.y * scale }
					const label = placed.item.kind === 'text' ? `Text: ${placed.item.text}` : 'Picture or signature'
					return (
						<div
							key={placed.id}
							role="button"
							tabIndex={0}
							aria-label={label}
							aria-pressed={selected === placed.id}
							className={selected === placed.id ? 'pdf-item selected' : 'pdf-item'}
							style={style}
							onPointerDown={(e) => down(e, placed)}
							onPointerMove={(e) => move(e, placed)}
							onPointerUp={up}
							onPointerCancel={up}
							onFocus={() => setSelected(placed.id)}
							onKeyDown={(e) => key(e, placed)}
						>
							{placed.item.kind === 'text' ? (
								<span style={{ fontSize: placed.item.size * scale, lineHeight: 1.2, whiteSpace: 'pre' }}>{placed.item.text}</span>
							) : (
								<PictureView item={placed.item} scale={scale} onFail={(reason) => setProblem(`A picture on this page can't be shown (${reason}). Remove it and use another.`)} />
							)}
						</div>
					)
				})}
			</div>
			{current === null ? null : (
				<div className="pdf-editor-tools">
					{current.item.kind === 'text' ? (
						<>
							<PillField aria-label="Edit the text" value={current.item.text} onChange={(value) => onEditText(current.id, value)} maxLength={200} />
							<PillSlider aria-label="Text size" min={6} max={72} step={1} value={current.item.size} onChange={(value) => onResize(current.id, value)} valueText={`${current.item.size} points`} />
						</>
					) : (
						<PillSlider aria-label="Picture width" min={20} max={Math.max(40, Math.round(size?.width ?? 600))} step={2} value={Math.round(current.item.width)} onChange={(value) => onResize(current.id, value)} valueText={`${Math.round(current.item.width)} points wide`} />
					)}
					<PillButton
						size="small"
						variant="outlined"
						onClick={() => {
							onRemove(current.id)
							setSelected(null)
						}}
					>
						Remove
					</PillButton>
				</div>
			)}
		</div>
	)
}
