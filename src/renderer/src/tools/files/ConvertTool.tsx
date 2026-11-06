import { useEffect, useMemo, useRef, useState } from 'react'
import { convertTargets, inputSupport } from '@shared/convert/backends'
import { ConvertRefusedError, convertPlan, explainFfmpegFailure, lastLine, pageFileName, type ConvertPlan } from '@shared/convert/commands'
import type { EngineInfo } from '@shared/engines/engineInfo'
import type { EngineExit, PickedFile } from '@shared/engines/engineJob'
import { formatBytes } from '@shared/format/formatBytes'
import { PDF_LIMITS } from '@shared/pdf/pdfCore'
import { panelId, tool, UtilityTool } from '@shared/toolbox/panelContent'
import { zing } from '../../api'
import { FileDropZone, FileRow, InfoBanner, PillButton, PillMenu, PillSlider } from '../../components'
import { describeFailure } from '../dev/parts'
import { ToolPage } from '../parts'
import './files.css'
import { newPdfWorkerClient } from './pdfEditClient'
import { handOffPdf, takeHandedPdf } from './pdfHandoff'
import { usePanelId } from '../panelId'
import { readGranted, saveResult } from './pdfHost'
import { createPreviewSession } from './pdfRender'
import { toPicture } from './pictureBytes'
import { useEngineJob } from './useEngineJob'

const DEFAULT_QUALITY = 80

type Stage =
	| { readonly kind: 'idle' }
	/** The engine this needs isn't downloaded: the person is told its name, size and source, and nothing is fetched until they press Download. */
	| { readonly kind: 'offer'; readonly engine: EngineInfo; readonly plan: ConvertPlan }
	| { readonly kind: 'downloading'; readonly engine: EngineInfo; readonly fraction: number }
	/** From the Convert click until the plan is running or the offer is shown: the file must not change under it. */
	| { readonly kind: 'preparing' }
	| { readonly kind: 'working'; readonly cancellable: boolean; readonly label?: string }
	| { readonly kind: 'done'; readonly outputs: readonly PickedFile[] }
	| { readonly kind: 'failed'; readonly message: string }
	| { readonly kind: 'cancelled'; readonly kept?: number }

const ENGINE_IDS = { ffmpeg: 'ffmpeg', magick: 'magick', pandoc: 'pandoc' } as const

function extensionOf(name: string): string {
	const dot = name.lastIndexOf('.')
	return dot < 0 ? '' : name.slice(dot + 1).toLowerCase()
}

/**
 * The words for an engine that exited non-zero. FFmpeg's stderr is translated; any other engine's last line is shown as
 * it is. The lines may contain file paths, so they appear here and nowhere else: not in a log, not stored.
 */
function describeEngineFailure(exit: EngineExit, plan: Extract<ConvertPlan, { kind: 'engine' }>, target: string): string {
	if (exit.error !== undefined) return `The converted file couldn't be put in Downloads: ${exit.error}`
	const stderr = exit.stderrTail.join('\n')
	const why = plan.engine === 'ffmpeg' ? explainFfmpegFailure(stderr, target) : lastLine(stderr)
	return `Conversion failed: ${why}`
}

/** Convert > images, documents, audio and video, with the engines Zing downloads on request. */
export function ConvertTool() {
	const job = useEngineJob()
	const [file, setFile] = useState<PickedFile | null>(null)
	const [choice, setChoice] = useState<string | null>(null)
	const [quality, setQuality] = useState(DEFAULT_QUALITY)
	const [stage, setStage] = useState<Stage>({ kind: 'idle' })
	const [problem, setProblem] = useState<string | null>(null)

	const ext = file === null ? '' : extensionOf(file.name)
	const support = file === null ? null : inputSupport(ext)
	const targets = useMemo(() => (file === null ? [] : convertTargets(ext)), [file, ext])
	const target = targets.find((t) => t.ext === choice) ?? targets[0]
	// A drop or a pick changes the file, so none is taken from the Convert click until the job ends or the offer is dismissed.
	const busy = stage.kind === 'preparing' || stage.kind === 'offer' || stage.kind === 'working' || stage.kind === 'downloading'

	useEffect(() => {
		const stop = zing.onEngineProgress((progress) => setStage((now) => (now.kind === 'downloading' && now.engine.id === progress.id ? { ...now, fraction: progress.fraction } : now)))
		return stop
	}, [])

	const take = (files: readonly PickedFile[]): void => {
		// A drop while a job runs would swap the file under it and lose its Cancel.
		if (busy) return
		const first = files[0]
		if (first === undefined) return
		setFile(first)
		setChoice(null)
		setStage({ kind: 'idle' })
		setProblem(files.length > 1 ? `Convert takes one file at a time, so only ${first.name} was used.` : null)
	}

	const fail = (error: unknown): void => {
		setStage({ kind: 'idle' })
		setProblem(describeFailure(error))
	}

	const pick = (): void => {
		zing.pickFiles(['image', 'video', 'audio', 'document']).then(take, fail)
	}

	// Convert's own PDF workers, ended when this tool closes; another panel's are never touched.
	const [pdfClient] = useState(newPdfWorkerClient)
	const [pdfViewer] = useState(createPreviewSession)
	useEffect(
		() => () => {
			pdfClient.stop()
			void pdfViewer.destroy()
		},
		[pdfClient, pdfViewer],
	)
	const ownPanel = usePanelId()
	const aborter = useRef(new AbortController())
	const stopPages = useRef(false)
	const runningPages = useRef(false)

	/** A PDF's pages as pictures, drawn by pdf.js and saved one at a time. No engine is involved, and the PDF is never handed to one. */
	const runPages = async (plan: Extract<ConvertPlan, { kind: 'pages' }>, name: string): Promise<void> => {
		stopPages.current = false
		aborter.current = new AbortController()
		const signal = aborter.current.signal
		runningPages.current = true
		setStage({ kind: 'working', cancellable: true, label: 'Opening the PDF…' })
		let preview: Awaited<ReturnType<typeof pdfViewer.open>>
		let sessionId: string
		try {
			const read = await readGranted(ownPanel, plan.token, signal)
			sessionId = read.sessionId
			preview = await pdfViewer.open(name, read.bytes)
		} catch (error) {
			runningPages.current = false
			throw error
		}
		try {
			if (preview.pageCount > PDF_LIMITS.maxConvertPages) {
				throw new ConvertRefusedError(`${name} has ${preview.pageCount} pages; Convert turns up to ${PDF_LIMITS.maxConvertPages} pages into pictures at a time. Split it in PDF Tools first.`)
			}
			const made: PickedFile[] = []
			for (let page = 0; page < preview.pageCount && !stopPages.current; page++) {
				setStage({ kind: 'working', cancellable: true, label: `Page ${page + 1} of ${preview.pageCount}` })
				const bytes = await preview.toImage(page, plan.target, plan.quality / 100)
				made.push(await saveResult(sessionId, pageFileName(plan.base, page + 1, plan.target), bytes, signal))
			}
			setStage(stopPages.current ? { kind: 'cancelled', kept: made.length } : { kind: 'done', outputs: made })
		} finally {
			runningPages.current = false
			await preview.destroy()
		}
	}

	const runPicture = async (plan: Extract<ConvertPlan, { kind: 'picture' }>, name: string): Promise<void> => {
		setStage({ kind: 'working', cancellable: false })
		const read = await readGranted(ownPanel, plan.token)
		const picture = await toPicture(name, read.bytes)
		const pdf = await pdfClient.imagesToPdf([{ name, format: picture.format, bytes: picture.bytes }])
		setStage({ kind: 'done', outputs: [await saveResult(read.sessionId, plan.output, pdf)] })
	}

	const execute = async (plan: ConvertPlan, targetExt: string): Promise<void> => {
		if (plan.kind === 'print') {
			setStage({ kind: 'working', cancellable: false })
			setStage({ kind: 'done', outputs: [await zing.printHtmlToPdf(plan.token, plan.output)] })
			return
		}
		if (plan.kind === 'pages') return runPages(plan, file?.name ?? 'The PDF')
		if (plan.kind === 'picture') return runPicture(plan, file?.name ?? 'The picture')
		setStage({ kind: 'working', cancellable: true })
		const exit = await job.run(ENGINE_IDS[plan.engine], { args: plan.args, outputs: plan.outputs })
		if (exit.cancelled) {
			setStage({ kind: 'cancelled' })
			return
		}
		const output = exit.outputs[0]
		if (exit.code === 0 && exit.error === undefined && output !== undefined) {
			setStage({ kind: 'done', outputs: [output] })
			return
		}
		setStage({ kind: 'failed', message: describeEngineFailure(exit, plan, targetExt) })
	}

	// A refusal or a failed start is the person's to read, not a crash.
	const guarded = async (work: () => Promise<void>): Promise<void> => {
		try {
			await work()
		} catch (error) {
			fail(error)
		}
	}

	const start = (): void => {
		if (file === null || target === undefined) return
		setProblem(null)
		setStage({ kind: 'preparing' })
		void guarded(async () => {
			let plan: ConvertPlan
			try {
				plan = convertPlan(file, target.ext, quality)
			} catch (error) {
				if (!(error instanceof ConvertRefusedError)) throw error
				setStage({ kind: 'idle' })
				setProblem(error.message)
				return
			}
			if (plan.kind === 'engine') {
				const engine = (await zing.listEngines()).find((e) => e.id === ENGINE_IDS[plan.engine])
				if (engine === undefined) throw new Error(`Zing has no engine called ${plan.engine}`)
				if (engine.installedVersion === undefined) {
					setStage({ kind: 'offer', engine, plan })
					return
				}
			}
			await execute(plan, target.ext)
		})
	}

	const download = (engine: EngineInfo, plan: ConvertPlan): void => {
		if (file === null || target === undefined) return
		setStage({ kind: 'downloading', engine, fraction: 0 })
		void guarded(async () => {
			const outcome = await zing.ensureEngine(engine.id)
			if (outcome === 'cancelled') {
				setStage({ kind: 'idle' })
				setProblem('Cancelled. Nothing was kept.')
				return
			}
			await execute(plan, target.ext)
		})
	}

	const openInPdfTools = (output: PickedFile): void => {
		handOffPdf(output)
		zing.openPanel(panelId(tool(UtilityTool.pdf)), { x: window.screenX + 560, y: window.screenY + 160 }).then((outcome) => {
			if (outcome.status !== 'full') return
			takeHandedPdf()
			setProblem('Three panels are already open. Close one, then open the PDF again.')
		}, fail)
	}

	const unavailable = support !== null && 'unavailable' in support ? support.unavailable : null

	return (
		<ToolPage subtitle="Images, documents, PDFs, audio and video. The converted file goes to your Downloads folder; the original is never changed.">
			<FileDropZone onFiles={take} onError={fail}>
				{({ targeted }) => (
					<div className={targeted ? 'convert-drop targeted' : 'convert-drop'}>
						{file === null ? <span className="convert-hint">Drop a file here</span> : <FileRow path={file.name} />}
						<PillButton variant="outlined" size="small" disabled={busy} onClick={pick}>
							{file === null ? 'Choose a file…' : 'Choose another'}
						</PillButton>
					</div>
				)}
			</FileDropZone>
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
			{unavailable === null ? null : <InfoBanner kind="warning">{`${file?.name ?? 'This file'} can't be converted: ${unavailable}.`}</InfoBanner>}
			{target === undefined ? null : (
				<div className="convert-options">
					<div className="convert-row">
						<span className="tool-label">Convert to</span>
						<PillMenu aria-label="Convert to" options={targets.map((t) => ({ value: t.ext, label: t.label }))} value={target.ext} onChange={setChoice} />
					</div>
					{target.lossy ? (
						<div className="convert-row">
							<span className="tool-label">Quality {quality}</span>
							<PillSlider aria-label="Quality" min={1} max={100} step={1} value={quality} onChange={setQuality} valueText={`${quality} out of 100`} />
						</div>
					) : null}
					{ext === 'pdf' ? <span className="convert-hint">Pages with JPEG 2000, JBIG2 or CCITT pictures (common in scans) may come out blank; the PDF viewer here does not decode those.</span> : null}
					{target.ext === 'pdf' ? <span className="convert-hint">The page is printed as UTF-8 text, with scripts and the network off.</span> : null}
					<div className="convert-row">
						<PillButton disabled={busy} onClick={start}>
							Convert
						</PillButton>
					</div>
				</div>
			)}
			<StageView
				stage={stage}
				onDownload={download}
				onCancelDownload={(engine) => void zing.cancelEngine(engine.id).catch(fail)}
				onCancelJob={() => {
					if (runningPages.current) {
						stopPages.current = true
						aborter.current.abort()
					}
					else void job.cancel().catch(fail)
				}}
				onOpenPdf={openInPdfTools}
				onDismiss={() => setStage({ kind: 'idle' })}
			/>
		</ToolPage>
	)
}

function StageView({ stage, onDownload, onCancelDownload, onCancelJob, onDismiss, onOpenPdf }: {
	stage: Stage
	onDownload: (engine: EngineInfo, plan: ConvertPlan) => void
	onCancelDownload: (engine: EngineInfo) => void
	onCancelJob: () => void
	onDismiss: () => void
	onOpenPdf: (output: PickedFile) => void
}) {
	switch (stage.kind) {
		case 'idle':
			return null
		case 'offer':
			return (
				<InfoBanner kind="info">
					<div className="convert-offer">
						<span>
							{`This needs ${stage.engine.name} ${stage.engine.version}, which isn't on this PC yet. It is a ${formatBytes(stage.engine.downloadBytes)} download from ${stage.engine.url}. Nothing downloads until you press Download; then your file is converted.`}
						</span>
						<div className="convert-actions">
							<PillButton size="small" aria-label={`Download ${stage.engine.name}`} onClick={() => onDownload(stage.engine, stage.plan)}>
								{`Download ${stage.engine.name}`}
							</PillButton>
							<PillButton size="small" variant="ghost" onClick={onDismiss}>
								Not now
							</PillButton>
						</div>
					</div>
				</InfoBanner>
			)
		case 'downloading':
			return (
				<InfoBanner kind="info">
					<div className="convert-offer">
						<span role="status">{`Downloading ${stage.engine.name}… ${Math.round(stage.fraction * 100)}%`}</span>
						<div className="convert-actions">
							<PillButton size="small" variant="outlined" aria-label={`Cancel ${stage.engine.name}`} onClick={() => onCancelDownload(stage.engine)}>
								Cancel
							</PillButton>
						</div>
					</div>
				</InfoBanner>
			)
		case 'preparing':
			return null
		case 'working':
			return (
				<InfoBanner kind="info">
					<div className="convert-offer">
						<span role="status">{stage.label ?? 'Converting…'}</span>
						{stage.cancellable ? (
							<div className="convert-actions">
								<PillButton size="small" variant="outlined" aria-label="Cancel conversion" onClick={onCancelJob}>
									Cancel
								</PillButton>
							</div>
						) : null}
					</div>
				</InfoBanner>
			)
		case 'done':
			return (
				<InfoBanner kind="info">
					<div className="convert-offer" role="status">
						<span>Saved to your Downloads folder:</span>
						{stage.outputs.map((output) => (
							<FileRow key={output.token} path={output.name} />
						))}
						{stage.outputs.length === 1 && stage.outputs[0]!.name.toLowerCase().endsWith('.pdf') ? (
							<div className="convert-actions">
								<PillButton size="small" variant="outlined" onClick={() => onOpenPdf(stage.outputs[0]!)}>
									Open in PDF Tools
								</PillButton>
							</div>
						) : null}
					</div>
				</InfoBanner>
			)
		case 'failed':
			return <InfoBanner kind="warning">{stage.message}</InfoBanner>
		case 'cancelled':
			return <InfoBanner kind="info">{stage.kept === undefined || stage.kept === 0 ? 'Cancelled. Nothing was kept.' : `Stopped. The ${stage.kept} ${stage.kept === 1 ? 'page' : 'pages'} already made ${stage.kept === 1 ? 'is' : 'are'} in your Downloads folder.`}</InfoBanner>
	}
}
