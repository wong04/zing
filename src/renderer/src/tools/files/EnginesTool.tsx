import { useCallback, useEffect, useState } from 'react'
import { describeEngine, type EngineInfo } from '@shared/engines/engineInfo'
import { zing } from '../../api'
import { InfoBanner, PillButton } from '../../components'
import { describeFailure } from '../dev/parts'
import { ToolPage } from '../parts'
import './files.css'

/** One engine row: what it is, how big, where from, and the buttons that act on it. */
function EngineRow({ engine, progress, busy, onDownload, onRemove, onUpdate }: {
	engine: EngineInfo
	progress: number | undefined
	busy: boolean
	onDownload: () => void
	onRemove: () => void
	onUpdate: () => void
}) {
	const installed = engine.installedVersion !== undefined
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
				{installed ? (
					<PillButton variant="outlined" size="small" disabled={busy} aria-label={`Remove ${engine.name}`} onClick={onRemove}>
						Remove
					</PillButton>
				) : (
					<PillButton size="small" disabled={busy} aria-label={`Download ${engine.name}`} onClick={onDownload}>
						Download
					</PillButton>
				)}
				{installed && engine.id === 'ytdlp' ? (
					<PillButton variant="ghost" size="small" disabled={busy} aria-label="Update yt-dlp" onClick={onUpdate}>
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
	const [progress, setProgress] = useState<Readonly<Record<string, number>>>({})
	const [working, setWorking] = useState<string | null>(null)
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
		const stop = zing.onEngineProgress((p) => setProgress((before) => ({ ...before, [p.id]: p.fraction })))
		return () => {
			mounted = false
			stop()
		}
	}, [])

	const act = async (id: string, work: () => Promise<string | null>): Promise<void> => {
		setWorking(id)
		setProblem(null)
		setNote(null)
		try {
			setNote(await work())
		} catch (error) {
			setProblem(describeFailure(error))
		} finally {
			setProgress((before) => Object.fromEntries(Object.entries(before).filter(([key]) => key !== id)))
			setWorking(null)
			await refresh().catch((error: unknown) => setProblem(describeFailure(error)))
		}
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
						progress={progress[engine.id]}
						busy={working !== null}
						onDownload={() => void act(engine.id, async () => (await zing.ensureEngine(engine.id), null))}
						onRemove={() => void act(engine.id, async () => (await zing.removeEngine(engine.id), null))}
						onUpdate={() =>
							void act(engine.id, async () => {
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
