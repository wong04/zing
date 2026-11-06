import { useEffect, useRef, useState } from 'react'
import { proceedAfterCheck, RunGate } from '@shared/convert/bgFlow'
import { outputFileName } from '@shared/convert/commands'
import type { ViewerOpened } from '@shared/convert/viewable'
import type { EngineInfo } from '@shared/engines/engineInfo'
import type { PickedFile } from '@shared/engines/engineJob'
import { formatBytes } from '@shared/format/formatBytes'
import { zing } from '../../api'
import { CopyFileButton, FileDropZone, FileRow, InfoBanner, PillButton } from '../../components'
import { describeFailure } from '../dev/parts'
import { usePanelId } from '../panelId'
import { ToolPage } from '../parts'
import { MODEL_URL, saveUrl, STAGE_TEXT, type RemoveMessage, type RemoveRequest, type RemoveStage } from './bgremoveProtocol'
import './files.css'

const MODEL_ID = 'bgmodel'
/** Room for " no background.png" inside a name the host accepts. */
const MAX_BASE_CHARACTERS = 80

type Stage =
	| { readonly kind: 'idle' }
	/** The model isn't downloaded: the person is told its size and source, and nothing is fetched until they press Download. */
	| { readonly kind: 'offer'; readonly engine: EngineInfo }
	| { readonly kind: 'downloading'; readonly engine: EngineInfo; readonly fraction: number }
	| { readonly kind: 'working'; readonly stage: RemoveStage; readonly fraction: number | undefined }
	| { readonly kind: 'done'; readonly result: PickedFile; readonly seconds: number }
	| { readonly kind: 'failed'; readonly message: string }
	| { readonly kind: 'cancelled' }

function savedName(pictureName: string): string {
	const dot = pictureName.lastIndexOf('.')
	const base = dot > 0 ? pictureName.slice(0, dot) : pictureName
	return outputFileName(`${[...base].slice(0, MAX_BASE_CHARACTERS).join('')} no background`, 'png')
}

/** Tool > Background Remover: a pre-trained model runs on this PC, in a worker; nothing is uploaded. */
export function BgRemoveTool() {
	const panelId = usePanelId()
	const [file, setFile] = useState<PickedFile | null>(null)
	const [stage, setStage] = useState<Stage>({ kind: 'idle' })
	const [problem, setProblem] = useState<string | null>(null)
	const worker = useRef<Worker | null>(null)
	// The picture's session on the host: what the worker is given instead of a path or a token. Revoked on Cancel, a new picture, the end of a run and closing the panel.
	const session = useRef<ViewerOpened | null>(null)
	const gate = useRef(new RunGate())
	const runId = useRef(0)
	// A closed panel must not go on to show or save a late result.
	const mounted = useRef(true)

	const busy = stage.kind === 'offer' || stage.kind === 'downloading' || stage.kind === 'working'

	const stopWorker = (): void => {
		worker.current?.terminate()
		worker.current = null
	}

	const endSession = (): void => {
		const open = session.current
		session.current = null
		if (open === null) return
		zing.closeViewerFile(open.id).catch((error: unknown) => {
			if (mounted.current) setProblem(describeFailure(error))
		})
	}

	// Closing the panel unmounts this (minimizing only hides it), and the worker and the session go with it.
	useEffect(() => {
		mounted.current = true
		const runs = gate.current
		return () => {
			mounted.current = false
			runs.cancel()
			stopWorker()
			endSession()
		}
	}, [])

	useEffect(() => {
		const stop = zing.onEngineProgress((progress) => setStage((now) => (now.kind === 'downloading' && now.engine.id === progress.id ? { ...now, fraction: progress.fraction } : now)))
		return stop
	}, [])

	const take = (files: readonly PickedFile[]): void => {
		// A drop while a run is going would swap the picture under it.
		if (busy) return
		const first = files[0]
		if (first === undefined) return
		endSession()
		setFile(first)
		setStage({ kind: 'idle' })
		setProblem(files.length > 1 ? `The background remover takes one picture at a time, so only ${first.name} was used.` : null)
	}

	const fail = (error: unknown): void => {
		gate.current.cancel()
		stopWorker()
		endSession()
		if (!mounted.current) return
		setStage({ kind: 'idle' })
		setProblem(describeFailure(error))
	}

	/** Starts a worker with one request; the worker is ended on its answer, on Cancel and when the panel closes. */
	const launch = (request: RemoveRequest, onMessage: (message: RemoveMessage) => void): void => {
		stopWorker()
		// `new Worker(new URL(...))` stays inline: that exact shape is what the bundler recognises. The check worker never
		// carries onnxruntime, which loads only when a removal really runs.
		const started = request.type === 'check' ? new Worker(new URL('./bgcheck.worker.ts', import.meta.url), { type: 'module' }) : new Worker(new URL('./bgremove.worker.ts', import.meta.url), { type: 'module' })
		worker.current = started
		started.onmessage = (event: MessageEvent<RemoveMessage>) => {
			if (event.data.type !== 'progress') stopWorker()
			onMessage(event.data)
		}
		started.onerror = (event) => {
			stopWorker()
			endSession()
			setStage({ kind: 'failed', message: event.message || 'The background remover stopped unexpectedly.' })
		}
		started.postMessage(request)
	}

	const run = (picture: PickedFile, opened: ViewerOpened): void => {
		setStage({ kind: 'working', stage: 'reading', fraction: undefined })
		launch({ type: 'run', fileUrl: opened.url, modelUrl: MODEL_URL, saveUrl: saveUrl(opened.id, savedName(picture.name)) }, (message) => {
			if (message.type === 'progress') {
				setStage({ kind: 'working', stage: message.stage, fraction: message.fraction })
				return
			}
			endSession()
			if (message.type === 'failed') setStage({ kind: 'failed', message: message.message })
			else if (message.type === 'done') setStage({ kind: 'done', result: { name: message.name, token: message.token }, seconds: message.seconds })
		})
	}

	const model = async (): Promise<{ installed: boolean; engine: EngineInfo }> => {
		const engine = (await zing.listEngines()).find((e) => e.id === MODEL_ID)
		if (engine === undefined) throw new Error('Zing has no background model in its engine list')
		return { installed: engine.installedVersion !== undefined, engine }
	}

	const start = (): void => {
		if (file === null) return
		const id = gate.current.begin()
		runId.current = id
		setProblem(null)
		setStage({ kind: 'working', stage: 'reading', fraction: undefined })
		const begin = async (): Promise<void> => {
			// The host judges the picture's first bytes and length here, and gives the worker a session in place of the file.
			const opened = await zing.openBackgroundPicture(panelId, file.token)
			if (!gate.current.isCurrent(id)) {
				zing.closeViewerFile(opened.id).catch(fail)
				return
			}
			session.current = opened
			launch({ type: 'check', fileUrl: opened.url }, (message) => {
				if (message.type === 'failed') {
					endSession()
					setStage({ kind: 'failed', message: message.message })
					return
				}
				if (message.type !== 'checked') return
				// The picture is judged first, so one that cannot be used never leads to a 170 MB offer.
				const found = model()
				let engine: EngineInfo | undefined
				proceedAfterCheck(gate.current, id, async () => {
					const result = await found
					engine = result.engine
					return result
				}, () => setStage({ kind: 'offer', engine: engine! }), () => run(file, opened)).catch(fail)
			})
		}
		begin().catch(fail)
	}

	const download = (engine: EngineInfo): void => {
		const opened = session.current
		if (file === null || opened === null) return
		const id = runId.current
		setStage({ kind: 'downloading', engine, fraction: 0 })
		const fetchModel = async (): Promise<void> => {
			const outcome = await zing.ensureEngine(engine.id)
			if (!gate.current.isCurrent(id) || !mounted.current) return
			if (outcome === 'cancelled') {
				endSession()
				setStage({ kind: 'idle' })
				setProblem('Cancelled. Nothing was kept.')
				return
			}
			run(file, opened)
		}
		fetchModel().catch(fail)
	}

	const cancelRun = (): void => {
		gate.current.cancel()
		stopWorker()
		endSession()
		setStage({ kind: 'cancelled' })
	}

	const dismissOffer = (): void => {
		gate.current.cancel()
		endSession()
		setStage({ kind: 'idle' })
	}

	return (
		<ToolPage subtitle="Cuts the background out of a PNG, JPEG or WebP picture, on this PC. The result is a PNG in your Downloads folder; the original is never changed.">
			<FileDropZone onFiles={take} onError={fail}>
				{({ targeted }) => (
					<div className={targeted ? 'convert-drop targeted' : 'convert-drop'}>
						{file === null ? <span className="convert-hint">Drop a picture here</span> : <FileRow path={file.name} />}
						<PillButton variant="outlined" size="small" disabled={busy} onClick={() => zing.pickFiles(['image']).then(take, fail)}>
							{file === null ? 'Choose a picture…' : 'Choose another'}
						</PillButton>
					</div>
				)}
			</FileDropZone>
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
			{file === null ? null : (
				<div className="convert-row">
					<PillButton disabled={busy} onClick={start}>
						Remove background
					</PillButton>
				</div>
			)}
			<StageView stage={stage} onCopyError={(error) => setProblem(describeFailure(error))} onDownload={download} onCancelDownload={(engine) => void zing.cancelEngine(engine.id).catch(fail)} onCancelRun={cancelRun} onDismiss={dismissOffer} />
		</ToolPage>
	)
}

function StageView({ stage, onDownload, onCancelDownload, onCancelRun, onDismiss, onCopyError }: {
	stage: Stage
	onCopyError: (error: Error) => void
	onDownload: (engine: EngineInfo) => void
	onCancelDownload: (engine: EngineInfo) => void
	onCancelRun: () => void
	onDismiss: () => void
}) {
	switch (stage.kind) {
		case 'idle':
			return null
		case 'offer':
			return (
				<InfoBanner kind="info">
					<div className="convert-offer">
						<span>
							{`This needs the ${stage.engine.name} (${stage.engine.version}), which isn't on this PC yet. It is a ${formatBytes(stage.engine.downloadBytes)} download from ${stage.engine.url}, a copy of the model hosted by the rembg project. Nothing downloads until you press Download; then your picture is processed here and nothing is uploaded.`}
						</span>
						<span>{`Licence: ${stage.engine.licence}. Check the terms for the model before you rely on a result for anything you publish or sell.`}</span>
						<div className="convert-actions">
							<PillButton size="small" aria-label={`Download ${stage.engine.name}`} onClick={() => onDownload(stage.engine)}>
								{`Download ${formatBytes(stage.engine.downloadBytes)}`}
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
						<span role="status">{`Downloading the ${stage.engine.name}… ${Math.round(stage.fraction * 100)}%`}</span>
						<div className="convert-actions">
							<PillButton size="small" variant="outlined" aria-label={`Cancel ${stage.engine.name}`} onClick={() => onCancelDownload(stage.engine)}>
								Cancel
							</PillButton>
						</div>
					</div>
				</InfoBanner>
			)
		case 'working':
			return (
				<InfoBanner kind="info">
					<div className="convert-offer">
						<span role="status">{`${STAGE_TEXT[stage.stage]}…${stage.fraction === undefined ? '' : ` ${Math.round(stage.fraction * 100)}%`}`}</span>
						{stage.fraction === undefined ? null : <progress className="bg-progress" value={stage.fraction} max={1} aria-label={STAGE_TEXT[stage.stage]} />}
						<div className="convert-actions">
							<PillButton size="small" variant="outlined" aria-label="Cancel background removal" onClick={onCancelRun}>
								Cancel
							</PillButton>
						</div>
					</div>
				</InfoBanner>
			)
		case 'done':
			return (
				<InfoBanner kind="info">
					<div className="convert-offer" role="status">
						<span>{`Saved to your Downloads folder (${stage.seconds.toFixed(1)} seconds):`}</span>
						<div className="convert-output">
							<FileRow path={stage.result.name} dragFile={stage.result} />
							<CopyFileButton file={stage.result} onError={onCopyError} />
						</div>
					</div>
				</InfoBanner>
			)
		case 'failed':
			return <InfoBanner kind="warning">{stage.message}</InfoBanner>
		case 'cancelled':
			return <InfoBanner kind="info">Cancelled. Nothing was kept.</InfoBanner>
	}
}
