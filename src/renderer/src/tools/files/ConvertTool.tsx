import { useEffect, useMemo, useState } from 'react'
import { convertTargets, inputSupport } from '@shared/convert/backends'
import { ConvertRefusedError, convertPlan, explainFfmpegFailure, lastLine, type ConvertPlan } from '@shared/convert/commands'
import type { EngineInfo } from '@shared/engines/engineInfo'
import type { EngineExit, PickedFile } from '@shared/engines/engineJob'
import { formatBytes } from '@shared/format/formatBytes'
import { zing } from '../../api'
import { FileDropZone, FileRow, InfoBanner, PillButton, PillMenu, PillSlider } from '../../components'
import { describeFailure } from '../dev/parts'
import { ToolPage } from '../parts'
import './files.css'
import { useEngineJob } from './useEngineJob'

const DEFAULT_QUALITY = 80

type Stage =
	| { readonly kind: 'idle' }
	/** The engine this needs isn't downloaded: the person is told its name, size and source, and nothing is fetched until they press Download. */
	| { readonly kind: 'offer'; readonly engine: EngineInfo; readonly plan: ConvertPlan }
	| { readonly kind: 'downloading'; readonly engine: EngineInfo; readonly fraction: number }
	/** From the Convert click until the plan is running or the offer is shown: the file must not change under it. */
	| { readonly kind: 'preparing' }
	| { readonly kind: 'working'; readonly cancellable: boolean }
	| { readonly kind: 'done'; readonly output: PickedFile }
	| { readonly kind: 'failed'; readonly message: string }
	| { readonly kind: 'cancelled' }

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

	const execute = async (plan: ConvertPlan, targetExt: string): Promise<void> => {
		if (plan.kind === 'print') {
			setStage({ kind: 'working', cancellable: false })
			setStage({ kind: 'done', output: await zing.printHtmlToPdf(plan.token, plan.output) })
			return
		}
		setStage({ kind: 'working', cancellable: true })
		const exit = await job.run(ENGINE_IDS[plan.engine], { args: plan.args, outputs: plan.outputs })
		if (exit.cancelled) {
			setStage({ kind: 'cancelled' })
			return
		}
		const output = exit.outputs[0]
		if (exit.code === 0 && exit.error === undefined && output !== undefined) {
			setStage({ kind: 'done', output })
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

	const unavailable = support !== null && 'unavailable' in support ? support.unavailable : null

	return (
		<ToolPage subtitle="Images, documents, audio and video. The converted file goes to your Downloads folder; the original is never changed.">
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
				onCancelJob={() => void job.cancel().catch(fail)}
				onDismiss={() => setStage({ kind: 'idle' })}
			/>
		</ToolPage>
	)
}

function StageView({ stage, onDownload, onCancelDownload, onCancelJob, onDismiss }: {
	stage: Stage
	onDownload: (engine: EngineInfo, plan: ConvertPlan) => void
	onCancelDownload: (engine: EngineInfo) => void
	onCancelJob: () => void
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
						<span role="status">Converting…</span>
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
