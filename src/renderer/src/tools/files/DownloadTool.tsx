import { useCallback, useEffect, useReducer, useState } from 'react'
import { describeEngine, type EngineInfo } from '@shared/engines/engineInfo'
import { InvalidMediaUrlError, ytdlpJob, type MediaMode } from '@shared/convert/ytdlpArgs'
import { zing } from '../../api'
import { InfoBanner, PillButton, PillField, SegmentedControl } from '../../components'
import { usePanelId } from '../panelId'
import { describeFailure } from '../dev/parts'
import { ToolPage } from '../parts'
import { downloadReducer, initialDownload } from './downloadState'
import './files.css'
import './download.css'

const NEEDED_ENGINES: readonly string[] = ['ytdlp', 'ffmpeg']

const MODES = [
	{ value: 'mp3', label: 'MP3 (audio)' },
	{ value: 'mp4', label: 'MP4 (video)' },
] as const

/** An engine the download needs and the person hasn't downloaded yet. Nothing is fetched until the button is pressed. */
function MissingEngine({ engine, fraction, busy, onDownload }: { engine: EngineInfo; fraction: number | undefined; busy: boolean; onDownload: () => void }) {
	return (
		<li className="download-missing">
			<span>
				<strong>{engine.name}</strong> is needed. {fraction === undefined ? describeEngine(engine) : `Downloading… ${Math.round(fraction * 100)}%`}
			</span>
			<small className="engine-source">From {engine.url}</small>
			<PillButton size="small" aria-label={`Download ${engine.name}`} disabled={busy} onClick={onDownload}>
				Download
			</PillButton>
		</li>
	)
}

/** The Download section: a video's audio or video through yt-dlp, and the current page saved as HTML. */
export function DownloadTool() {
	const panelId = usePanelId()
	const [state, dispatch] = useReducer(downloadReducer, initialDownload)
	const [url, setUrl] = useState('')
	const [mode, setMode] = useState<MediaMode>('mp3')
	const [engines, setEngines] = useState<readonly EngineInfo[] | null>(null)
	const [fetching, setFetching] = useState<{ readonly id: string; readonly fraction: number } | null>(null)
	const [problem, setProblem] = useState<string | null>(null)
	const [note, setNote] = useState<string | null>(null)
	const [updating, setUpdating] = useState(false)
	const [savingPage, setSavingPage] = useState(false)

	const refresh = useCallback(async (): Promise<void> => {
		setEngines(await zing.listEngines())
	}, [])

	useEffect(() => {
		let mounted = true
		zing.listEngines().then(
			(list) => {
				if (mounted) setEngines(list)
			},
			(error: unknown) => setProblem(describeFailure(error)),
		)
		const stops = [
			zing.onEngineProgress((p) => setFetching((now) => (now?.id === p.id ? { id: p.id, fraction: p.fraction } : now))),
			zing.onEngineOutput((output) => dispatch({ type: 'output', jobId: output.jobId, lines: output.lines })),
			zing.onEngineExit((exit) => dispatch({ type: 'exit', exit })),
		]
		return () => {
			mounted = false
			for (const stop of stops) stop()
		}
	}, [])

	const running = state.phase === 'running'
	const missing = (engines ?? []).filter((engine) => NEEDED_ENGINES.includes(engine.id) && engine.installedVersion === undefined)
	const ready = engines !== null && missing.length === 0

	const start = (): void => {
		if (running || !ready) return
		setProblem(null)
		setNote(null)
		let job
		try {
			job = ytdlpJob(url, mode)
		} catch (error) {
			if (!(error instanceof InvalidMediaUrlError)) throw error
			setProblem(error.message)
			return
		}
		dispatch({ type: 'starting' })
		zing.runEngine('ytdlp', panelId, job).then(
			(jobId) => dispatch({ type: 'started', jobId }),
			(error: unknown) => dispatch({ type: 'start-failed', message: describeFailure(error) }),
		)
	}

	const cancel = (): void => {
		if (state.phase !== 'running' || state.jobId === null) return
		zing.cancelEngineJob(state.jobId).catch((error: unknown) => setProblem(describeFailure(error)))
	}

	const fetchEngine = (id: string): void => {
		setFetching({ id, fraction: 0 })
		setProblem(null)
		zing
			.ensureEngine(id)
			.then(
				() => undefined,
				(error: unknown) => setProblem(describeFailure(error)),
			)
			.finally(() => {
				setFetching(null)
				refresh().catch((error: unknown) => setProblem(describeFailure(error)))
			})
	}

	const updateYtDlp = (): void => {
		setUpdating(true)
		setProblem(null)
		zing
			.updateYtDlp()
			.then(
				(update) => setNote(update.changed ? `yt-dlp updated to ${update.version}. Press Download to try again.` : `yt-dlp is already the newest release (${update.version}).`),
				(error: unknown) => setProblem(describeFailure(error)),
			)
			.finally(() => setUpdating(false))
	}

	const savePage = (): void => {
		setSavingPage(true)
		setProblem(null)
		zing
			.savePage()
			.then(
				(file) => dispatch({ type: 'page-saved', name: file.name }),
				(error: unknown) => setProblem(describeFailure(error)),
			)
			.finally(() => setSavingPage(false))
	}

	return (
		<ToolPage subtitle="Save a video's sound as MP3 or the video as MP4. Nothing is fetched until you press a button.">
			<InfoBanner kind="info">
				Only the one video in the link is downloaded, never a playlist or a channel. Zing sends no cookies or sign-ins from your browser, and if a site asks to confirm
				you are not a bot, the download stops there.
			</InfoBanner>
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
			{note === null ? null : <InfoBanner kind="info">{note}</InfoBanner>}
			{missing.length === 0 ? null : (
				<ul className="download-missing-list">
					{missing.map((engine) => (
						<MissingEngine
							key={engine.id}
							engine={engine}
							fraction={fetching?.id === engine.id ? fetching.fraction : undefined}
							busy={fetching !== null}
							onDownload={() => fetchEngine(engine.id)}
						/>
					))}
				</ul>
			)}
			<PillField label="Video link" value={url} onChange={setUrl} onSubmit={start} placeholder="https://" disabled={running} spellCheck={false} autoComplete="off" />
			<div className="download-row">
				<SegmentedControl options={MODES} value={mode} onChange={setMode} aria-label="Format" width={220} />
				<PillButton disabled={running || !ready || url.trim() === ''} onClick={start}>
					Download
				</PillButton>
				{running ? (
					<PillButton variant="outlined" disabled={state.jobId === null} onClick={cancel}>
						Cancel
					</PillButton>
				) : null}
			</div>
			{state.phase === 'running' ? (
				<div className="download-progress" role="status">
					<progress aria-label="Download progress" max={100} value={state.percent ?? undefined} />
					<span>{state.finishing ? 'Finishing the file…' : state.percent === null ? 'Starting…' : `Downloading… ${Math.round(state.percent)}%`}</span>
				</div>
			) : null}
			{state.phase === 'cancelled' ? <InfoBanner kind="info">Cancelled. Nothing was kept.</InfoBanner> : null}
			{state.phase === 'done' ? <InfoBanner kind="info">Saved {state.file} to your Downloads folder.</InfoBanner> : null}
			{state.phase === 'failed' ? (
				<InfoBanner kind="warning">
					{state.failure.message}
					{state.failure.offerUpdate ? (
						<div className="download-update">
							<PillButton size="small" variant="outlined" aria-label="Update yt-dlp" disabled={updating} onClick={updateYtDlp}>
								Update yt-dlp
							</PillButton>
						</div>
					) : null}
				</InfoBanner>
			) : null}
			<div className="download-page">
				<span>Save the page open in the browser as an HTML file. Only the page itself is saved, not its images or styles.</span>
				<PillButton size="small" variant="outlined" disabled={savingPage} onClick={savePage}>
					Save this page
				</PillButton>
			</div>
			{state.saved.length === 0 ? null : (
				<ul className="download-saved" aria-label="Saved files">
					{state.saved.map((name, index) => (
						<li key={`${index}-${name}`}>{name}</li>
					))}
				</ul>
			)}
		</ToolPage>
	)
}
