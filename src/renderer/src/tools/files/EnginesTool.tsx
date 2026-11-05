import { useCallback, useEffect, useState } from 'react'
import { describeEngine, describeOutcome, engineActions, type EngineInfo } from '@shared/engines/engineInfo'
import { zing } from '../../api'
import { InfoBanner, PillButton } from '../../components'
import { describeFailure } from '../dev/parts'
import { ToolPage } from '../parts'
import './files.css'

/** One engine row: what it is, how big, where from, and the buttons that act on it. */
function EngineRow({ engine, progress, busy, onDownload, onCancel, onRemove, onUpdate }: {
	engine: EngineInfo
	/** 0 to 1 while this row's download runs; undefined otherwise. */
	progress: number | undefined
	busy: boolean
	onDownload: () => void
	onCancel: () => void
	onRemove: () => void
	onUpdate: () => void
}) {
	const actions = engineActions(engine, { downloading: progress !== undefined, busy })
	return (
		<li className="engine-row">
			<div className="engine-head">
				<strong>{engine.name}</strong>
				<span className="engine-status">{progress === undefined ? describeEngine(engine) : `Downloading… ${Math.round(progress * 100)}%`}</span>
			</div>
			{/* The address is text on purpose: a person can read it and fetch the file by hand. */}
			<small className="engine-source">From {engine.url}</small>
			<small className="engine-licence">Licence: {engine.licence}</small>
			<div className="engine-actions">
				{actions.download ? (
					<PillButton size="small" aria-label={`Download ${engine.name}`} onClick={onDownload}>
						Download
					</PillButton>
				) : null}
				{actions.cancel ? (
					<PillButton variant="outlined" size="small" aria-label={`Cancel ${engine.name}`} onClick={onCancel}>
						Cancel
					</PillButton>
				) : null}
				{actions.remove ? (
					<PillButton variant="outlined" size="small" aria-label={`Remove ${engine.name}`} onClick={onRemove}>
						Remove
					</PillButton>
				) : null}
				{actions.update ? (
					<PillButton variant="ghost" size="small" aria-label="Update yt-dlp" onClick={onUpdate}>
						Update
					</PillButton>
				) : null}
			</div>
		</li>
	)
}

/** Settings > Files > Engines: the programs and the model Zing downloads when a tool first needs them. */
export function EnginesTool() {
	const [engines, setEngines] = useState<readonly EngineInfo[] | null>(null)
	const [downloading, setDownloading] = useState<{ readonly id: string; readonly fraction: number } | null>(null)
	const [working, setWorking] = useState(false)
	const [problem, setProblem] = useState<string | null>(null)
	const [note, setNote] = useState<string | null>(null)

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
		const stop = zing.onEngineProgress((p) => setDownloading((now) => (now?.id === p.id ? { id: p.id, fraction: p.fraction } : now)))
		return () => {
			mounted = false
			stop()
		}
	}, [])

	const run = async (work: () => Promise<string | null>): Promise<void> => {
		setWorking(true)
		setProblem(null)
		setNote(null)
		try {
			setNote(await work())
		} catch (error) {
			setProblem(describeFailure(error))
		} finally {
			setDownloading(null)
			setWorking(false)
			await refresh().catch((error: unknown) => setProblem(describeFailure(error)))
		}
	}

	// The row counts as downloading from the click, before the first progress event arrives.
	const download = (id: string): void => {
		setDownloading({ id, fraction: 0 })
		void run(async () => describeOutcome(await zing.ensureEngine(id)))
	}

	return (
		<ToolPage subtitle="Programs Zing downloads the first time a tool needs one. Nothing downloads until you press Download.">
			<InfoBanner kind="info">Engines are tools, not your data: Zing keeps nothing about how you use them, and having them doesn't change the privacy banner.</InfoBanner>
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
			{note === null ? null : <InfoBanner kind="info">{note}</InfoBanner>}
			<ul className="engine-list">
				{(engines ?? []).map((engine) => (
					<EngineRow
						key={engine.id}
						engine={engine}
						progress={downloading?.id === engine.id ? downloading.fraction : undefined}
						busy={working}
						onDownload={() => download(engine.id)}
						onCancel={() => void zing.cancelEngine(engine.id)}
						onRemove={() => void run(async () => (await zing.removeEngine(engine.id), null))}
						onUpdate={() =>
							void run(async () => {
								const update = await zing.updateYtDlp()
								return update.changed ? `yt-dlp updated to ${update.version}.` : `yt-dlp is already the newest release (${update.version}).`
							})
						}
					/>
				))}
			</ul>
		</ToolPage>
	)
}
