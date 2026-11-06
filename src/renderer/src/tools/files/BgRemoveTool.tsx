import { useEffect, useRef, useState } from 'react'
import { outputFileName } from '@shared/convert/commands'
import type { EngineInfo } from '@shared/engines/engineInfo'
import type { PickedFile } from '@shared/engines/engineJob'
import { formatBytes } from '@shared/format/formatBytes'
import { zing } from '../../api'
import { FileDropZone, FileRow, InfoBanner, PillButton } from '../../components'
import { describeFailure } from '../dev/parts'
import { ToolPage } from '../parts'
import { LOCAL_ORIGIN, STAGE_TEXT, type RemoveMessage, type RemoveRequest, type RemoveStage } from './bgremoveProtocol'
import './files.css'

const MODEL_ID = 'bgmodel'
/** Served by the host to Zing's own views only (LocalFiles.cs); the picture by its token, the model by its engine id. */
const MODEL_URL = `${LOCAL_ORIGIN}/zing-local/model/${MODEL_ID}`
const fileUrl = (token: string): string => `${LOCAL_ORIGIN}/zing-local/file/${token}`
/** Room for " no background.png" inside a name the host accepts. */
const MAX_BASE_CHARACTERS = 80

type Stage =
	| { readonly kind: 'idle' }
	/** The model isn't downloaded: the person is told its size and source, and nothing is fetched until they press Download. */
	| { readonly kind: 'offer'; readonly engine: EngineInfo }
	| { readonly kind: 'downloading'; readonly engine: EngineInfo; readonly fraction: number }
	| { readonly kind: 'working'; readonly stage: RemoveStage; readonly fraction: number | undefined }
	| { readonly kind: 'saving' }
	| { readonly kind: 'done'; readonly output: PickedFile; readonly seconds: number }
	| { readonly kind: 'failed'; readonly message: string }
	| { readonly kind: 'cancelled' }

function savedName(pictureName: string): string {
	const dot = pictureName.lastIndexOf('.')
	const base = dot > 0 ? pictureName.slice(0, dot) : pictureName
	return outputFileName(`${[...base].slice(0, MAX_BASE_CHARACTERS).join('')} no background`, 'png')
}

/** The PNG as base64 for the host's channel, read by the browser off the main thread's own work. */
function toBase64(png: ArrayBuffer): Promise<string> {
	return new Promise((resolve, reject) => {
		const reader = new FileReader()
		reader.onload = () => resolve(String(reader.result).slice(String(reader.result).indexOf(',') + 1))
		reader.onerror = () => reject(reader.error ?? new Error('The picture could not be read back'))
		reader.readAsDataURL(new Blob([png], { type: 'image/png' }))
	})
}

/** Tool > Background Remover: a pre-trained model runs on this PC, in a worker; nothing is uploaded. */
export function BgRemoveTool() {
	const [file, setFile] = useState<PickedFile | null>(null)
	const [stage, setStage] = useState<Stage>({ kind: 'idle' })
	const [problem, setProblem] = useState<string | null>(null)
	const worker = useRef<Worker | null>(null)
	// What the host-bound save should say, kept off the render: a closed panel must not save a late result.
	const mounted = useRef(true)

	const busy = stage.kind === 'offer' || stage.kind === 'downloading' || stage.kind === 'working' || stage.kind === 'saving'

	const stopWorker = (): void => {
		worker.current?.terminate()
		worker.current = null
	}

	// Closing the panel unmounts this (minimizing only hides it), and the worker goes with it.
	useEffect(() => {
		mounted.current = true
		return () => {
			mounted.current = false
			stopWorker()
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
		setFile(first)
		setStage({ kind: 'idle' })
		setProblem(files.length > 1 ? `The background remover takes one picture at a time, so only ${first.name} was used.` : null)
	}

	const fail = (error: unknown): void => {
		stopWorker()
		setStage({ kind: 'idle' })
		setProblem(describeFailure(error))
	}

	const save = async (picture: PickedFile, png: ArrayBuffer, seconds: number): Promise<void> => {
		setStage({ kind: 'saving' })
		const output = await zing.savePng(savedName(picture.name), await toBase64(png))
		if (mounted.current) setStage({ kind: 'done', output, seconds })
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
			setStage({ kind: 'failed', message: event.message || 'The background remover stopped unexpectedly.' })
		}
		started.postMessage(request)
	}

	const run = (picture: PickedFile): void => {
		setStage({ kind: 'working', stage: 'reading', fraction: undefined })
		launch({ type: 'run', fileUrl: fileUrl(picture.token), modelUrl: MODEL_URL }, (message) => {
			if (message.type === 'progress') setStage({ kind: 'working', stage: message.stage, fraction: message.fraction })
			else if (message.type === 'failed') setStage({ kind: 'failed', message: message.message })
			else if (message.type === 'done') save(picture, message.png, message.seconds).catch(fail)
		})
	}

	/** The picture is judged first, so one that cannot be used never leads to a 170 MB offer. */
	const afterCheck = async (picture: PickedFile): Promise<void> => {
		const model = (await zing.listEngines()).find((e) => e.id === MODEL_ID)
		if (model === undefined) throw new Error('Zing has no background model in its engine list')
		if (!mounted.current) return
		if (model.installedVersion === undefined) setStage({ kind: 'offer', engine: model })
		else run(picture)
	}

	const start = (): void => {
		if (file === null) return
		setProblem(null)
		setStage({ kind: 'working', stage: 'reading', fraction: undefined })
		launch({ type: 'check', fileUrl: fileUrl(file.token) }, (message) => {
			if (message.type === 'failed') setStage({ kind: 'failed', message: message.message })
			else if (message.type === 'checked') afterCheck(file).catch(fail)
		})
	}

	const download = (engine: EngineInfo): void => {
		if (file === null) return
		setStage({ kind: 'downloading', engine, fraction: 0 })
		const fetchModel = async (): Promise<void> => {
			const outcome = await zing.ensureEngine(engine.id)
			if (!mounted.current) return
			if (outcome === 'cancelled') {
				setStage({ kind: 'idle' })
				setProblem('Cancelled. Nothing was kept.')
				return
			}
			run(file)
		}
		fetchModel().catch(fail)
	}

	const cancelRun = (): void => {
		stopWorker()
		setStage({ kind: 'cancelled' })
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
			<StageView stage={stage} onDownload={download} onCancelDownload={(engine) => void zing.cancelEngine(engine.id).catch(fail)} onCancelRun={cancelRun} onDismiss={() => setStage({ kind: 'idle' })} />
		</ToolPage>
	)
}

function StageView({ stage, onDownload, onCancelDownload, onCancelRun, onDismiss }: {
	stage: Stage
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
		case 'saving':
			return (
				<InfoBanner kind="info">
					<span role="status">Saving to your Downloads folder…</span>
				</InfoBanner>
			)
		case 'done':
			return (
				<InfoBanner kind="info">
					<div className="convert-offer" role="status">
						<span>{`Saved to your Downloads folder (${stage.seconds.toFixed(1)} seconds):`}</span>
						<FileRow path={stage.output.name} />
					</div>
				</InfoBanner>
			)
		case 'failed':
			return <InfoBanner kind="warning">{stage.message}</InfoBanner>
		case 'cancelled':
			return <InfoBanner kind="info">Cancelled. Nothing was kept.</InfoBanner>
	}
}
