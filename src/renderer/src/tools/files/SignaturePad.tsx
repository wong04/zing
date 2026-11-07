import { useEffect, useRef, useState, type PointerEvent } from 'react'
import { PillButton, PillField } from '../../components'
import { canvasToPng } from './pictureBytes'

const WIDTH = 320
const HEIGHT = 120
const INK = '#14213d'

/** Signature (a picture of ink, not a cryptographic signature) drawn with the pointer or typed. */
export interface SignaturePadProps {
	readonly onUse: (png: Uint8Array, width: number, height: number) => void
	readonly onCancel: () => void
	readonly onError: (error: unknown) => void
}

/** The box around the ink, so the placed picture is the signature and not a wide empty rectangle. */
function inkBounds(canvas: HTMLCanvasElement): { x: number; y: number; width: number; height: number } | null {
	const { width, height } = canvas
	const data = canvas.getContext('2d', { willReadFrequently: true })!.getImageData(0, 0, width, height).data
	let left = width
	let top = height
	let right = -1
	let bottom = -1
	for (let y = 0; y < height; y++) {
		for (let x = 0; x < width; x++) {
			if (data[(y * width + x) * 4 + 3]! === 0) continue
			left = Math.min(left, x)
			right = Math.max(right, x)
			top = Math.min(top, y)
			bottom = Math.max(bottom, y)
		}
	}
	return right < 0 ? null : { x: left, y: top, width: right - left + 1, height: bottom - top + 1 }
}

export function SignaturePad({ onUse, onCancel, onError }: SignaturePadProps) {
	const canvas = useRef<HTMLCanvasElement>(null)
	const stroke = useRef<{ x: number; y: number } | null>(null)
	const [typed, setTyped] = useState('')
	const [empty, setEmpty] = useState(true)

	useEffect(() => {
		const context = canvas.current!.getContext('2d')!
		context.lineWidth = 2.5
		context.lineCap = 'round'
		context.lineJoin = 'round'
		context.strokeStyle = INK
	}, [])

	const at = (event: PointerEvent<HTMLCanvasElement>): { x: number; y: number } => {
		const box = event.currentTarget.getBoundingClientRect()
		return { x: ((event.clientX - box.left) / box.width) * WIDTH, y: ((event.clientY - box.top) / box.height) * HEIGHT }
	}

	const down = (event: PointerEvent<HTMLCanvasElement>): void => {
		event.currentTarget.setPointerCapture(event.pointerId)
		stroke.current = at(event)
		setTyped('')
	}
	const move = (event: PointerEvent<HTMLCanvasElement>): void => {
		const from = stroke.current
		if (from === null) return
		const to = at(event)
		const context = canvas.current!.getContext('2d')!
		context.beginPath()
		context.moveTo(from.x, from.y)
		context.lineTo(to.x, to.y)
		context.stroke()
		stroke.current = to
		setEmpty(false)
	}

	const clear = (): void => {
		canvas.current!.getContext('2d')!.clearRect(0, 0, WIDTH, HEIGHT)
		setEmpty(true)
	}

	const type = (text: string): void => {
		setTyped(text)
		const context = canvas.current!.getContext('2d')!
		context.clearRect(0, 0, WIDTH, HEIGHT)
		setEmpty(text.trim() === '')
		if (text.trim() === '') return
		context.save()
		context.font = "italic 44px 'Segoe Script', 'Brush Script MT', cursive"
		context.fillStyle = INK
		context.textBaseline = 'middle'
		context.fillText(text, 12, HEIGHT / 2, WIDTH - 24)
		context.restore()
	}

	const use = (): void => {
		const source = canvas.current!
		const box = inkBounds(source)
		if (box === null) return
		const pad = 4
		const cut = document.createElement('canvas')
		cut.width = box.width + pad * 2
		cut.height = box.height + pad * 2
		cut.getContext('2d')!.drawImage(source, box.x, box.y, box.width, box.height, pad, pad, box.width, box.height)
		canvasToPng(cut, 'The signature').then((png) => onUse(png, cut.width, cut.height), onError)
	}

	return (
		<div className="pdf-signature">
			<canvas ref={canvas} width={WIDTH} height={HEIGHT} className="pdf-signature-canvas" aria-label="Draw your signature here with the pointer" onPointerDown={down} onPointerMove={move} onPointerUp={() => (stroke.current = null)} onPointerCancel={() => (stroke.current = null)} />
			<PillField label="Or type it" value={typed} onChange={type} placeholder="Your name" maxLength={40} />
			<div className="convert-actions">
				<PillButton size="small" disabled={empty} onClick={use}>
					Place on page
				</PillButton>
				<PillButton size="small" variant="outlined" onClick={clear}>
					Clear
				</PillButton>
				<PillButton size="small" variant="ghost" onClick={onCancel}>
					Cancel
				</PillButton>
			</div>
		</div>
	)
}
