import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { outputFileName } from '@shared/convert/commands'
import type { PickedFile } from '@shared/engines/engineJob'
import { PDF_LIMITS, PdfEditError, parsePageRanges, type FormFieldInfo, type RightAngle } from '@shared/pdf/pdfCore'
import { zing } from '../../api'
import { FileDropZone, FileRow, InfoBanner, PillButton, PillField } from '../../components'
import { describeFailure } from '../dev/parts'
import { ToolPage } from '../parts'
import './files.css'
import { PdfPageEditor } from './PdfPageEditor'
import { onHandedPdf, takeHandedPdf } from './pdfHandoff'
import { readGranted, saveBytes } from './pdfHost'
import {
	addFile,
	addItem,
	amend,
	checkSessionBytes,
	commit,
	deletePages,
	editText,
	emptyModel,
	moveItem,
	movePage,
	redo,
	removeItem,
	resizeItem,
	rotatePages,
	setFormValue,
	startHistory,
	toPlan,
	toPlanRange,
	undo,
	type History,
	type Model,
	type PageEntry,
} from './pdfModel'
import { buildInWorker, inspectInWorker, stopPdfWork } from './pdfEditClient'
import { openPreview, type PdfPreview } from './pdfRender'

interface Opened {
	readonly name: string
	readonly bytes: Uint8Array
	readonly preview: PdfPreview
	readonly fields: readonly FormFieldInfo[]
}

const THUMB_WIDTH = 110
const MAX_DRAWS = 3

// Page pictures are drawn a few at a time, and only for pages on screen, so a file of 500 pages costs what the screen shows.
let drawing = 0
const waitingToDraw: Array<() => void> = []

function acquireDraw(): Promise<() => void> {
	const release = (() => {
		let done = false
		return () => {
			if (done) return
			done = true
			drawing--
			waitingToDraw.shift()?.()
		}
	})()
	return new Promise((resolve) => {
		const start = (): void => {
			drawing++
			resolve(release)
		}
		if (drawing < MAX_DRAWS) start()
		else waitingToDraw.push(start)
	})
}

function PageThumb({ preview, index, turn, onError }: { preview: PdfPreview; index: number; turn: number; onError: (error: unknown) => void }) {
	const box = useRef<HTMLSpanElement>(null)
	const canvas = useRef<HTMLCanvasElement>(null)
	const [visible, setVisible] = useState(false)

	useEffect(() => {
		const observer = new IntersectionObserver((entries) => setVisible(entries.some((e) => e.isIntersecting)), { rootMargin: '150px' })
		observer.observe(box.current!)
		return () => observer.disconnect()
	}, [])

	useEffect(() => {
		if (!visible) return
		const target = canvas.current!
		let cancelled = false
		let run: ReturnType<PdfPreview['draw']> | undefined
		let release: (() => void) | undefined
		void acquireDraw().then((done) => {
			release = done
			if (cancelled) {
				done()
				return
			}
			run = preview.draw(index, target, THUMB_WIDTH, turn)
			run.done.then(
				() => done(),
				(error: unknown) => {
					done()
					onError(error)
				},
			)
		})
		return () => {
			cancelled = true
			run?.cancel()
			release?.()
			// Pages scrolled out of sight give their pixels back.
			target.width = 0
			target.height = 0
		}
	}, [visible, preview, index, turn, onError])

	return (
		<span ref={box} className="pdf-thumb">
			<canvas ref={canvas} className="pdf-thumb-canvas" aria-hidden="true" />
		</span>
	)
}

const withoutExtension = (name: string): string => name.replace(/\.pdf$/i, '')

/** Merge, split, reorder, turn and delete pages; put text, pictures and a signature on a page; fill forms. On this PC, on a copy, until saved. */
export function PdfTool() {
	const [history, setHistory] = useState<History>(() => startHistory(emptyModel()))
	const historyRef = useRef(history)
	const [opened, setOpened] = useState<readonly Opened[]>([])
	const openedRef = useRef<readonly Opened[]>([])
	const [selected, setSelected] = useState<readonly number[]>([])
	const [focused, setFocused] = useState<number | null>(null)
	const [editing, setEditing] = useState<number | null>(null)
	const [busy, setBusy] = useState<string | null>(null)
	const [problem, setProblem] = useState<string | null>(null)
	const [saved, setSaved] = useState<readonly string[]>([])
	const [announcement, setAnnouncement] = useState('')
	const [ranges, setRanges] = useState('')
	const cells = useRef(new Map<number, HTMLElement>())
	const refocus = useRef(false)
	const lastAmend = useRef<string | null>(null)

	const model = history.present
	const used = [...new Set(model.pages.map((p) => p.source))]
	const single = used.length === 1 ? opened[used[0]!] : undefined
	const fields = single?.fields ?? []

	const apply = (next: History): void => {
		historyRef.current = next
		setHistory(next)
	}
	const fail = (error: unknown): void => setProblem(describeFailure(error))

	/** Runs an edit; a refusal is shown to the person, not thrown. */
	const edit = (change: (m: Model) => Model, said?: string): void => {
		try {
			lastAmend.current = null
			apply(commit(historyRef.current, change(historyRef.current.present)))
			setProblem(null)
			setSaved([])
			if (said !== undefined) setAnnouncement(said)
		} catch (error) {
			fail(error)
		}
	}

	/** A run of small changes to one thing counts as one step for Undo. */
	const tweak = (key: string, change: (m: Model) => Model): void => {
		const now = historyRef.current
		const next = change(now.present)
		apply(lastAmend.current === key ? amend(now, next) : commit(now, next))
		lastAmend.current = key
		setSaved([])
	}

	const addOpened = async (file: PickedFile): Promise<void> => {
		if (!/\.pdf$/i.test(file.name)) throw new PdfEditError(`${file.name} isn't a PDF. This tool opens PDF files; Convert makes a PDF from a picture or a saved web page.`)
		const current = openedRef.current
		if (current.length >= PDF_LIMITS.maxMergeFiles) throw new PdfEditError(`This tool opens at most ${PDF_LIMITS.maxMergeFiles} files at once.`)
		const bytes = await readGranted(file.token)
		checkSessionBytes(current.reduce((sum, o) => sum + o.bytes.length, 0), bytes.length, file.name)
		const info = await inspectInWorker({ name: file.name, bytes })
		const preview = await openPreview(file.name, bytes)
		try {
			const source = current.length
			const next = commit(historyRef.current, addFile(historyRef.current.present, { source, name: file.name, pageCount: info.pageCount }))
			openedRef.current = [...current, { name: file.name, bytes, preview, fields: info.fields }]
			setOpened(openedRef.current)
			apply(next)
			setSaved([])
			setAnnouncement(`${file.name} added: ${info.pageCount} ${info.pageCount === 1 ? 'page' : 'pages'}.`)
		} catch (error) {
			void preview.destroy()
			throw error
		}
	}

	const take = (files: readonly PickedFile[]): void => {
		if (busy !== null || files.length === 0) return
		setBusy('Opening…')
		setProblem(null)
		void (async () => {
			try {
				for (const file of files) await addOpened(file)
			} catch (error) {
				fail(error)
			} finally {
				setBusy(null)
			}
		})()
	}
	const takeRef = useRef(take)
	useEffect(() => {
		takeRef.current = take
	})

	useEffect(() => {
		const waiting = takeHandedPdf()
		if (waiting !== undefined) takeRef.current([waiting])
		const stop = onHandedPdf((file) => takeRef.current([file]))
		return stop
	}, [])

	useEffect(
		() => () => {
			// Nothing is kept after the panel closes.
			stopPdfWork()
			for (const o of openedRef.current) void o.preview.destroy()
		},
		[],
	)

	const pick = (): void => {
		zing.pickFiles(['pdf']).then(take, fail)
	}

	const targets = (): readonly number[] => (selected.length > 0 ? selected : focused === null ? [] : [focused])
	const positionOf = (key: number): number => model.pages.findIndex((p) => p.key === key) + 1

	useLayoutEffect(() => {
		if (!refocus.current || focused === null) return
		refocus.current = false
		cells.current.get(focused)?.focus()
	})

	const doMove = (key: number, by: -1 | 1): void => {
		const to = positionOf(key) + by
		if (to < 1 || to > model.pages.length) {
			setAnnouncement(by < 0 ? 'Already the first page.' : 'Already the last page.')
			return
		}
		refocus.current = true
		edit((m) => movePage(m, key, by), `Page moved to position ${to} of ${model.pages.length}.`)
	}
	const doTurn = (angle: RightAngle): void => {
		const keys = targets()
		if (keys.length === 0) return
		edit((m) => rotatePages(m, keys, angle), `${keys.length === 1 ? 'Page' : `${keys.length} pages`} turned ${angle} degrees.`)
	}
	const doDelete = (): void => {
		const keys = targets()
		if (keys.length === 0) return
		const first = Math.min(...keys.map(positionOf))
		edit((m) => deletePages(m, keys), `${keys.length === 1 ? 'Page' : `${keys.length} pages`} deleted. ${model.pages.length - keys.length} left.`)
		setSelected([])
		const after = model.pages.filter((p) => !keys.includes(p.key))
		setFocused(after[Math.min(first - 1, after.length - 1)]?.key ?? null)
		refocus.current = true
	}
	const doUndo = (): void => {
		lastAmend.current = null
		apply(undo(historyRef.current))
		setAnnouncement('Undone.')
	}
	const doRedo = (): void => {
		lastAmend.current = null
		apply(redo(historyRef.current))
		setAnnouncement('Redone.')
	}

	const toggle = (key: number): void => setSelected((now) => (now.includes(key) ? now.filter((k) => k !== key) : [...now, key]))

	const focusCell = (key: number | undefined): void => {
		if (key === undefined) return
		setFocused(key)
		refocus.current = true
	}

	const columns = (): number => {
		const tops = model.pages.map((p) => cells.current.get(p.key)?.offsetTop ?? 0)
		const first = tops.findIndex((t) => t !== tops[0])
		return first < 0 ? Math.max(1, tops.length) : first
	}

	const onCellKey = (event: KeyboardEvent<HTMLElement>, entry: PageEntry): void => {
		const at = model.pages.findIndex((p) => p.key === entry.key)
		const go = (to: number): void => {
			event.preventDefault()
			focusCell(model.pages[Math.max(0, Math.min(model.pages.length - 1, to))]?.key)
		}
		if (event.ctrlKey && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
			event.preventDefault()
			doMove(entry.key, event.key === 'ArrowLeft' ? -1 : 1)
			return
		}
		switch (event.key) {
			case 'ArrowLeft':
				return go(at - 1)
			case 'ArrowRight':
				return go(at + 1)
			case 'ArrowUp':
				return go(at - columns())
			case 'ArrowDown':
				return go(at + columns())
			case 'Home':
				return go(0)
			case 'End':
				return go(model.pages.length - 1)
			case ' ':
				event.preventDefault()
				toggle(entry.key)
				setAnnouncement(selected.includes(entry.key) ? `Page ${at + 1} not selected.` : `Page ${at + 1} selected.`)
				return
			case 'Enter':
				event.preventDefault()
				setEditing(entry.key)
				return
			case 'Delete':
				event.preventDefault()
				doDelete()
				return
			case 'r':
			case 'R':
				event.preventDefault()
				doTurn(90)
		}
	}

	const result = (name: string): string => outputFileName(name, 'pdf')
	const baseName = (): string => withoutExtension(opened[used[0] ?? 0]?.name ?? 'document')

	const sourcesFor = (): { name: string; bytes: Uint8Array }[] =>
		openedRef.current.map((o, i) => ({ name: o.name, bytes: used.includes(i) ? o.bytes : new Uint8Array() }))

	const save = (): void => {
		if (busy !== null || model.pages.length === 0) return
		setBusy('Saving…')
		setProblem(null)
		const name = result(used.length > 1 ? 'merged' : `${baseName()}-edited`)
		void (async () => {
			try {
				const bytes = await buildInWorker(sourcesFor(), toPlan(model))
				const placed = await saveBytes(name, bytes)
				setSaved([placed.name])
				setAnnouncement(`Saved ${placed.name} to your Downloads folder.`)
			} catch (error) {
				fail(error)
			} finally {
				setBusy(null)
			}
		})()
	}

	const split = (): void => {
		if (busy !== null) return
		setProblem(null)
		let parts
		try {
			parts = parsePageRanges(ranges, model.pages.length)
		} catch (error) {
			fail(error)
			return
		}
		setBusy('Splitting…')
		void (async () => {
			const names: string[] = []
			try {
				for (const [i, range] of parts.entries()) {
					const bytes = await buildInWorker(sourcesFor(), toPlanRange(model, range))
					names.push((await saveBytes(result(`${baseName()}-part-${String(i + 1).padStart(2, '0')}`), bytes)).name)
				}
			} catch (error) {
				fail(error)
			} finally {
				setSaved(names)
				setBusy(null)
			}
		})()
	}

	const cancel = (): void => {
		stopPdfWork()
		setBusy(null)
		setProblem('Stopped. Nothing more was saved.')
	}

	const editorEntry = editing === null ? undefined : model.pages.find((p) => p.key === editing)

	const body = editorEntry ? (
		<PdfPageEditor
			preview={opened[editorEntry.source]!.preview}
			index={editorEntry.page - 1}
			position={positionOf(editorEntry.key)}
			turn={editorEntry.rotate}
			items={model.items.filter((i) => i.page === editorEntry.key)}
			onAdd={(item) => edit((m) => addItem(m, editorEntry.key, item), 'Added to the page.')}
			onMove={(id, to) => tweak(`move${id}`, (m) => moveItem(m, id, to))}
			onResize={(id, size) => tweak(`size${id}`, (m) => resizeItem(m, id, size))}
			onEditText={(id, text) => tweak(`text${id}`, (m) => editText(m, id, text))}
			onRemove={(id) => edit((m) => removeItem(m, id), 'Removed.')}
			onClose={() => {
				setEditing(null)
				focusCell(editorEntry.key)
			}}
		/>
	) : (
		<>
			<div className="pdf-toolbar" role="toolbar" aria-label="Page actions">
				<PillButton size="small" variant="outlined" disabled={history.past.length === 0} onClick={doUndo}>
					Undo
				</PillButton>
				<PillButton size="small" variant="outlined" disabled={history.future.length === 0} onClick={doRedo}>
					Redo
				</PillButton>
				<PillButton size="small" variant="outlined" disabled={targets().length === 0} onClick={() => doTurn(270)}>
					Turn left
				</PillButton>
				<PillButton size="small" variant="outlined" disabled={targets().length === 0} onClick={() => doTurn(90)}>
					Turn right
				</PillButton>
				<PillButton size="small" variant="outlined" disabled={focused === null} onClick={() => focused !== null && doMove(focused, -1)}>
					Move earlier
				</PillButton>
				<PillButton size="small" variant="outlined" disabled={focused === null} onClick={() => focused !== null && doMove(focused, 1)}>
					Move later
				</PillButton>
				<PillButton size="small" variant="outlined" disabled={targets().length === 0} onClick={doDelete}>
					Delete
				</PillButton>
				<PillButton size="small" variant="outlined" disabled={focused === null} onClick={() => focused !== null && setEditing(focused)}>
					Edit page…
				</PillButton>
			</div>
			<span className="convert-hint">Arrow keys move between pages, Space selects, Ctrl+Arrow moves the page, R turns it, Delete removes it, Enter edits it.</span>
			<ul className="pdf-grid" role="listbox" aria-label="Pages" aria-multiselectable="true">
				{model.pages.map((entry, i) => {
					const file = opened[entry.source]!
					const isSelected = selected.includes(entry.key)
					const label = `Page ${i + 1}${opened.length > 1 ? `, page ${entry.page} of ${file.name}` : ''}${entry.rotate === 0 ? '' : `, turned ${entry.rotate} degrees`}${model.items.some((p) => p.page === entry.key) ? ', has added items' : ''}`
					return (
						<li
							key={entry.key}
							ref={(element) => {
								if (element) cells.current.set(entry.key, element)
								else cells.current.delete(entry.key)
							}}
							role="option"
							aria-selected={isSelected}
							aria-label={label}
							tabIndex={(focused ?? model.pages[0]?.key) === entry.key ? 0 : -1}
							className={isSelected ? 'pdf-cell selected' : 'pdf-cell'}
							onFocus={() => setFocused(entry.key)}
							onClick={(e) => {
								setFocused(entry.key)
								if (e.ctrlKey || e.metaKey) toggle(entry.key)
								else setSelected([entry.key])
							}}
							onDoubleClick={() => setEditing(entry.key)}
							onKeyDown={(e) => onCellKey(e, entry)}
						>
							<PageThumb preview={file.preview} index={entry.page - 1} turn={entry.rotate} onError={fail} />
							<span className="pdf-cell-number">{i + 1}</span>
						</li>
					)
				})}
			</ul>
			{fields.length > 0 ? (
				<section className="pdf-section" aria-label="Form fields">
					<span className="tool-label">Form fields</span>
					{fields.map((field) => (
						<FormRow key={field.name} field={field} value={model.form[field.name]} onChange={(value) => tweak(`form${field.name}`, (m) => setFormValue(m, field.name, value))} />
					))}
					<span className="convert-hint">The form stays a form: saved fields can still be changed in a PDF reader.</span>
				</section>
			) : null}
			<section className="pdf-section" aria-label="Split">
				<PillField label="Split out pages" placeholder="1-3, 5" value={ranges} onChange={setRanges} onSubmit={split} />
				<PillButton size="small" variant="outlined" disabled={busy !== null || ranges.trim() === ''} onClick={split}>
					Save each range as its own file
				</PillButton>
			</section>
		</>
	)

	return (
		<ToolPage subtitle="Merge PDFs, split them, reorder, turn and delete pages, put text, pictures and a signature on a page, and fill forms. It all happens on this PC, on a copy; the original is never changed.">
			<FileDropZone onFiles={take} onError={fail}>
				{({ targeted }) => (
					<div className={targeted ? 'convert-drop targeted' : 'convert-drop'}>
						{opened.length === 0 ? <span className="convert-hint">Drop PDFs here</span> : <span className="convert-hint">{`${opened.length} ${opened.length === 1 ? 'file' : 'files'}, ${model.pages.length} ${model.pages.length === 1 ? 'page' : 'pages'}`}</span>}
						<PillButton variant="outlined" size="small" disabled={busy !== null || editing !== null} onClick={pick}>
							{opened.length === 0 ? 'Choose PDFs…' : 'Add another PDF…'}
						</PillButton>
					</div>
				)}
			</FileDropZone>
			<details className="pdf-limits">
				<summary>What this can and can&apos;t do</summary>
				<ul>
					<li>Text already in a PDF can&apos;t be changed in place. A PDF stores each letter at a position, not a flowing paragraph. You can add new text on top.</li>
					<li>A signature here is a picture you draw or type, placed on the page. It is not a digital signature: nothing proves who added it or that the page wasn&apos;t changed afterwards.</li>
					<li>Merging, splitting and deleting pages build a new file from the pages that stay. That drops bookmarks and turns form fields into plain pictures; a deleted page&apos;s content is not in the result. Reordering, turning, adding items and filling a form edit the file itself and keep both.</li>
					<li>A filled form is not flattened: its fields stay fillable.</li>
					<li>Password-protected PDFs are refused; Zing won&apos;t remove protection. Damaged files are refused by name.</li>
					<li>{`Files up to ${PDF_LIMITS.maxReadBytes / (1024 * 1024)} MB each, ${PDF_LIMITS.maxSessionBytes / (1024 * 1024)} MB open together, ${PDF_LIMITS.maxPages} pages in a file, ${PDF_LIMITS.maxMergeFiles} files in a merge. Results up to ${PDF_LIMITS.maxSaveBytes / (1024 * 1024)} MB.`}</li>
					<li>{`Undo takes back the last ${PDF_LIMITS.undoSteps} edits. Nothing is kept when you close this panel; save first.`}</li>
					<li>Added text is Helvetica (Western European letters). Previews don&apos;t draw JPEG 2000 or JBIG2 pictures; saving doesn&apos;t touch them.</li>
				</ul>
			</details>
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
			{opened.length === 0 ? null : body}
			{opened.length === 0 || editorEntry ? null : (
				<div className="convert-row">
					<PillButton disabled={busy !== null} onClick={save}>
						{used.length > 1 ? 'Save merged PDF' : 'Save PDF'}
					</PillButton>
					<span className="convert-hint">Goes to your Downloads folder; a name that is taken gets a number.</span>
				</div>
			)}
			{busy === null ? null : (
				<InfoBanner kind="info">
					<div className="convert-offer">
						<span role="status">{busy}</span>
						<div className="convert-actions">
							<PillButton size="small" variant="outlined" onClick={cancel}>
								Cancel
							</PillButton>
						</div>
					</div>
				</InfoBanner>
			)}
			{saved.length === 0 ? null : (
				<InfoBanner kind="info">
					<div className="convert-offer" role="status">
						<span>Saved to your Downloads folder:</span>
						{saved.map((name) => (
							<FileRow key={name} path={name} />
						))}
					</div>
				</InfoBanner>
			)}
			<div className="pdf-live" role="status" aria-live="polite">
				{announcement}
			</div>
		</ToolPage>
	)
}

function FormRow({ field, value, onChange }: { field: FormFieldInfo; value: string | undefined; onChange: (value: string) => void }) {
	const shown = value ?? field.value
	if (field.readOnly) return <span className="convert-hint">{`${field.name}: ${shown} (read only)`}</span>
	if (field.kind === 'checkbox') {
		return (
			<label className="pdf-form-row">
				<input type="checkbox" checked={shown === 'true'} onChange={(e) => onChange(String(e.target.checked))} />
				{field.name}
			</label>
		)
	}
	if (field.kind === 'text') return <PillField label={field.name} value={shown} onChange={onChange} />
	return (
		<label className="pdf-form-row">
			{field.name}
			<select value={shown} onChange={(e) => onChange(e.target.value)} aria-label={field.name}>
				<option value="" disabled>
					Choose…
				</option>
				{(field.options ?? []).map((option) => (
					<option key={option} value={option}>
						{option}
					</option>
				))}
			</select>
		</label>
	)
}
