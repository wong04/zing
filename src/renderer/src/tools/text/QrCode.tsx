import { useEffect, useMemo, useRef, useState } from 'react'
import { encodeQr, QrTooLongError, type QrModules } from '@shared/devtools/qr'
import { InfoBanner, PillButton } from '../../components'
import { TextArea, ToolPage } from '../parts'
import './text.css'

const quietZone = 4
const pixelsPerModule = 8

type Code = { readonly modules: QrModules } | { readonly error: string }

function encode(text: string): Code {
	try {
		return { modules: encodeQr(text) }
	} catch (error) {
		if (error instanceof QrTooLongError) return { error: error.message }
		throw error
	}
}

export function QrCode() {
	const [text, setText] = useState('')
	const canvas = useRef<HTMLCanvasElement>(null)
	const code = useMemo(() => (text === '' ? null : encode(text)), [text])
	const modules = code && 'modules' in code ? code.modules : null
	const [copyError, setCopyError] = useState<string | null>(null)
	const [copiedModules, setCopiedModules] = useState<typeof modules>(null)
	const copied = modules !== null && copiedModules === modules

	useEffect(() => {
		if (!copiedModules) return
		const timer = setTimeout(() => setCopiedModules(null), 2000)
		return () => clearTimeout(timer)
	}, [copiedModules])

	useEffect(() => {
		const el = canvas.current
		if (!el || !modules) return
		const context = el.getContext('2d')
		if (!context) throw new Error('A 2d canvas context is unavailable')
		const side = (modules.length + quietZone * 2) * pixelsPerModule
		el.width = side
		el.height = side
		context.fillStyle = '#fff'
		context.fillRect(0, 0, side, side)
		context.fillStyle = '#000'
		modules.forEach((row, y) =>
			row.forEach((dark, x) => {
				if (dark) context.fillRect((x + quietZone) * pixelsPerModule, (y + quietZone) * pixelsPerModule, pixelsPerModule, pixelsPerModule)
			}),
		)
	}, [modules])

	const copyImage = async (): Promise<void> => {
		const el = canvas.current
		if (!el) return
		try {
			const blob = await new Promise<Blob>((resolve, reject) =>
				el.toBlob((b) => (b ? resolve(b) : reject(new Error('The canvas produced no image'))), 'image/png'),
			)
			await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })])
			setCopyError(null)
			setCopiedModules(modules)
		} catch (error) {
			setCopiedModules(null)
			setCopyError(error instanceof Error ? error.message : String(error))
		}
	}

	return (
		<ToolPage subtitle="Turn any text or link into a scannable code.">
			<TextArea label="Text" value={text} onChange={setText} />
			{code && 'error' in code ? <InfoBanner kind="warning">{code.error}</InfoBanner> : null}
			{modules ? (
				<>
					<div className="qr-frame">
						<canvas ref={canvas} aria-label="QR code" />
					</div>
					<div>
						<PillButton variant="outlined" size="small" onClick={() => void copyImage()}>
							{copied ? 'Copied' : 'Copy image'}
						</PillButton>
					</div>
					{copyError ? <InfoBanner kind="warning">{`Could not copy the image: ${copyError}`}</InfoBanner> : null}
				</>
			) : null}
		</ToolPage>
	)
}
