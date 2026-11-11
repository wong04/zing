import { useCallback, useEffect, useRef, useState } from 'react'
import { conversionFraction, durationOf, elapsedOf } from '@shared/convert/ffmpegText'
import { extensionOf, viewPlan, type ViewerOpened } from '@shared/convert/viewable'
import { describeEngine, type EngineInfo } from '@shared/engines/engineInfo'
import type { EngineExit, PickedFile } from '@shared/engines/engineJob'
import { formatBytes } from '@shared/format/formatBytes'
import { panelId, Section, section } from '@shared/toolbox/panelContent'
import { zing } from '../../api'
import { FileDropZone, InfoBanner, PillButton, ToolEmptyState } from '../../components'
import { usePanelId } from '../panelId'
import { usePanelInput } from '../panelInput'
import { handedFile } from './handedFile'
import { PanelIcon } from '../../panels/panelIcon'
import { describeFailure } from '../dev/parts'
import './files.css'
import { playStream } from './streamPlayer'

const VIEWER_CONTENT = panelId(section(Section.viewer))
const CONVERT_PANEL = panelId(section(Section.convert))

type Notice =
	| { readonly kind: 'unsupported'; readonly name: string }
	| { readonly kind: 'engine'; readonly picked: PickedFile; readonly engine: EngineInfo; readonly fraction: number | undefined }
	| { readonly kind: 'failed'; readonly message: string }

interface Showing {
	readonly opened: ViewerOpened
	/** The copy is complete (or there is none to wait for). */
	readonly ready: boolean
	/** 0 to 1 while a conversion runs and says how far it is. */
	readonly fraction: number | undefined
}

const engineFor = (kind: 'image' | 'video' | 'audio'): string => (kind === 'image' ? 'magick' : 'ffmpeg')

function failureText(name: string, exit: EngineExit): string {
	const detail = exit.error ?? exit.stderrTail[exit.stderrTail.length - 1] ?? `the engine stopped with code ${exit.code}`
	return `Couldn't convert ${name}: ${detail}`
}

/** A video that is being converted: played from the growing copy as it is made. */
function StreamingVideo({ opened, duration, onError }: { opened: ViewerOpened; duration: () => number | undefined; onError: (error: Error) => void }) {
	const video = useRef<HTMLVideoElement>(null)
	useEffect(() => {
		const element = video.current
		if (!element) return
		return playStream(element, { url: opened.url, duration, onError })
	}, [opened.url, duration, onError])
	return <video ref={video} className="viewer-media" controls aria-label={opened.name} data-testid="viewer-video" />
}

function Stage({ file, duration, onError }: { file: Showing; duration: () => number | undefined; onError: (error: Error) => void }) {
	const { opened, ready } = file
	if (opened.stream) return <StreamingVideo opened={opened} duration={duration} onError={onError} />
	if (!ready) return null
	switch (opened.show) {
		case 'image':
			return <img className="viewer-media" src={opened.url} alt={opened.name} data-testid="viewer-image" />
		case 'video':
			return <video className="viewer-media" src={opened.url} controls aria-label={opened.name} data-testid="viewer-video" onError={() => onError(new Error(`Chromium can't play ${opened.name}`))} />
		case 'audio':
			return <audio className="viewer-audio" src={opened.url} controls aria-label={opened.name} data-testid="viewer-audio" onError={() => onError(new Error(`Chromium can't play ${opened.name}`))} />
		case 'pdf':
			// No sandbox attribute: Chromium's PDF viewer refuses to run in a sandboxed frame. The host keeps a link in the PDF from
			// loading anything or opening a window (RendererView), which viewer.spec checks.
			return <iframe className="viewer-media viewer-pdf" src={opened.url} title={opened.name} data-testid="viewer-pdf" />
	}
}

/** Files & Media > Files: shows a picture, video, sound or PDF, converting a temporary copy of what Chromium can't show. */
export function ViewerTool() {
	const panel = usePanelId()
	const [file, setFile] = useState<Showing | null>(null)
	const [notice, setNotice] = useState<Notice | null>(null)
	const current = useRef<Showing | null>(null)
	useEffect(() => {
		current.current = file
	}, [file])
	// Events can arrive before `viewer:open` has answered with the job's id, so they are kept until it has.
	const job = useRef<string | undefined>(undefined)
	const early = useRef(new Map<string, EngineExit>())
	const total = useRef<number | undefined>(undefined)

	useEffect(() => {
		const finish = (exit: EngineExit): void => {
			const showing = current.current
			if (exit.code === 0 && exit.error === undefined) {
				setFile((now) => (now ? { ...now, ready: true, fraction: 1 } : now))
				return
			}
			setNotice({ kind: 'failed', message: failureText(showing?.opened.name ?? 'the file', exit) })
			setFile(null)
			// The host has dropped the copy; this forgets the address too.
			if (showing) void zing.closeViewerFile(showing.opened.id)
		}
		const offOutput = zing.onEngineOutput((output) => {
			if (job.current !== undefined && job.current !== output.jobId) return
			for (const line of output.lines) {
				total.current = durationOf(line.text) ?? total.current
				const elapsed = elapsedOf(line.text)
				const fraction = elapsed === undefined ? undefined : conversionFraction(elapsed, total.current)
				if (fraction !== undefined) setFile((now) => (now && !now.ready ? { ...now, fraction } : now))
			}
		})
		const offExit = zing.onEngineExit((exit) => {
			if (job.current === undefined) {
				early.current.set(exit.jobId, exit)
				return
			}
			if (job.current !== exit.jobId || exit.cancelled) return
			finish(exit)
		})
		return () => {
			offOutput()
			offExit()
			const showing = current.current
			if (showing) void zing.closeViewerFile(showing.opened.id)
		}
	}, [])

	const close = useCallback(async (): Promise<void> => {
		const showing = current.current
		if (!showing) return
		setFile(null)
		job.current = undefined
		await zing.closeViewerFile(showing.opened.id)
	}, [])

	const open = useCallback(
		async (picked: PickedFile): Promise<void> => {
			setNotice(null)
			const plan = viewPlan(extensionOf(picked.name))
			if (plan.kind === 'unsupported') {
				await close()
				setNotice({ kind: 'unsupported', name: picked.name })
				return
			}
			if (plan.kind !== 'native') {
				const id = engineFor(plan.kind)
				const engine = (await zing.listEngines()).find((e) => e.id === id)
				if (!engine) throw new Error(`The host has no engine called ${id}`)
				if (engine.installedVersion === undefined) {
					await close()
					setNotice({ kind: 'engine', picked, engine, fraction: undefined })
					return
				}
			}
			job.current = undefined
			total.current = undefined
			early.current.clear()
			const opened = await zing.openViewerFile(picked.token, panel)
			job.current = opened.jobId
			const finished = opened.jobId === undefined ? undefined : early.current.get(opened.jobId)
			setFile({ opened, ready: opened.jobId === undefined, fraction: undefined })
			if (finished?.code === 0 && finished.error === undefined) setFile((now) => (now ? { ...now, ready: true, fraction: 1 } : now))
			else if (finished && !finished.cancelled) {
				setNotice({ kind: 'failed', message: failureText(opened.name, finished) })
				setFile(null)
				await zing.closeViewerFile(opened.id)
			}
		},
		[close, panel],
	)

	const openChecked = useCallback(
		(picked: PickedFile): void => {
			open(picked).catch((error: unknown) => {
				setFile(null)
				setNotice({ kind: 'failed', message: describeFailure(error) })
			})
		},
		[open],
	)

	// A file opened from another app: shown as if it had been dropped here, once per handoff.
	const handoff = usePanelInput()
	const seenHandoff = useRef<number | undefined>(undefined)
	useEffect(() => {
		if (!handoff || handoff.serial === seenHandoff.current) return
		seenHandoff.current = handoff.serial
		openChecked(handedFile(handoff.value))
	}, [handoff, openChecked])

	const choose = (): void => {
		zing.pickFiles(['any']).then(
			(files) => {
				if (files[0]) openChecked(files[0])
			},
			(error: unknown) => setNotice({ kind: 'failed', message: describeFailure(error) }),
		)
	}

	const download = (shown: Extract<Notice, { kind: 'engine' }>): void => {
		setNotice({ ...shown, fraction: 0 })
		zing.ensureEngine(shown.engine.id).then(
			(outcome) => {
				if (outcome === 'installed') openChecked(shown.picked)
				else setNotice({ ...shown, fraction: undefined })
			},
			(error: unknown) => setNotice({ kind: 'failed', message: describeFailure(error) }),
		)
	}

	useEffect(
		() =>
			zing.onEngineProgress((p) => {
				setNotice((now) => (now?.kind === 'engine' && now.engine.id === p.id && now.fraction !== undefined ? { ...now, fraction: p.fraction } : now))
			}),
		[],
	)

	const openConvert = (): void => void zing.openPanel(CONVERT_PANEL, { x: window.screenX + 560, y: window.screenY + 160 })

	const duration = useCallback(() => total.current, [])
	const streamFailed = useCallback((error: Error): void => {
		setNotice({ kind: 'failed', message: error.message })
	}, [])

	return (
		<FileDropZone onFiles={(files) => files[0] && openChecked(files[0])} onError={(error) => setNotice({ kind: 'failed', message: describeFailure(error) })}>
			{({ targeted }) => (
				<div className="viewer">
					{notice?.kind === 'unsupported' ? (
						<InfoBanner kind="warning">
							<span data-testid="viewer-unsupported">{notice.name} is a kind of file the viewer can't show.</span>{' '}
							<PillButton size="small" variant="outlined" onClick={openConvert}>
								Open Convert
							</PillButton>
						</InfoBanner>
					) : null}
					{notice?.kind === 'failed' ? <InfoBanner kind="warning">{notice.message}</InfoBanner> : null}
					{notice?.kind === 'engine' ? (
						<InfoBanner kind="info">
							<div data-testid="viewer-needs-engine">
								Showing {notice.picked.name} needs {notice.engine.name}. {describeEngine(notice.engine)}. Nothing downloads until you press Download.
							</div>
							<div className="viewer-actions">
								{notice.fraction === undefined ? (
									<PillButton size="small" aria-label={`Download ${notice.engine.name}`} onClick={() => download(notice)}>
										Download {formatBytes(notice.engine.downloadBytes)}
									</PillButton>
								) : (
									<>
										<span>Downloading… {Math.round(notice.fraction * 100)}%</span>
										<PillButton size="small" variant="outlined" aria-label={`Cancel ${notice.engine.name}`} onClick={() => void zing.cancelEngine(notice.engine.id)}>
											Cancel
										</PillButton>
									</>
								)}
							</div>
						</InfoBanner>
					) : null}
					{file ? (
						<>
							<div className="viewer-bar">
								<span className="viewer-name" title={file.opened.name}>
									{file.opened.name}
								</span>
								<PillButton size="small" variant="outlined" aria-label="Close file" onClick={() => void close()}>
									Close file
								</PillButton>
							</div>
							<div className="viewer-stage" data-testid="viewer-stage">
								<Stage file={file} duration={duration} onError={streamFailed} />
							</div>
							{file.ready ? null : (
								<div className="viewer-progress" role="progressbar" aria-label="Converting" aria-valuemin={0} aria-valuemax={100} aria-valuenow={file.fraction === undefined ? undefined : Math.round(file.fraction * 100)} data-testid="viewer-progress">
									<div className="viewer-progress-bar" style={{ width: `${Math.round((file.fraction ?? 0) * 100)}%` }} />
									<span>{file.fraction === undefined ? 'Converting a temporary copy…' : `Converting a temporary copy… ${Math.round(file.fraction * 100)}%`}</span>
								</div>
							)}
							<small className="viewer-note">{file.opened.jobId === undefined ? 'Shown from where it is.' : 'Shown from a temporary copy that is deleted when you close the file.'}</small>
						</>
					) : (
						<ToolEmptyState
							icon={<PanelIcon content={VIEWER_CONTENT} size={44} />}
							title="View a file"
							message="Drop a file here or choose one. Pictures, video, sound and PDFs open here, including HEIC, MOV and MKV, which are converted in a temporary copy that is deleted when you close the file."
							targeted={targeted}
						>
							<PillButton onClick={choose}>Choose a file</PillButton>
						</ToolEmptyState>
					)}
				</div>
			)}
		</FileDropZone>
	)
}
