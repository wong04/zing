import { formatBytes } from '../format/formatBytes'

// What the host's EngineStore reports (host/Zing.Core/Engines/EngineInfo.cs).

export interface EngineInfo {
	readonly id: string
	/** The name shown to a person ("FFmpeg"). */
	readonly name: string
	/** The pinned release. */
	readonly version: string
	/** Where it is fetched from. Shown as text so it can be fetched by hand; never a link. */
	readonly url: string
	readonly licence: string
	/** The size of the download, in bytes. */
	readonly downloadBytes: number
	/** Absent while the engine isn't downloaded. */
	readonly installedVersion?: string
	/** On disk, after unpacking; 0 while not downloaded. */
	readonly installedBytes: number
}

export interface EngineProgress {
	readonly id: string
	/** 0 to 1. */
	readonly fraction: number
}

/** 'cancelled' is an ordinary answer: a person pressed Cancel, and nothing was kept. */
export type EnsureOutcome = 'installed' | 'cancelled'

export interface YtDlpUpdate {
	readonly version: string
	readonly changed: boolean
}

export function describeEngine(engine: EngineInfo): string {
	if (engine.installedVersion === undefined) return `Not downloaded · ${formatBytes(engine.downloadBytes)} download`
	return `Downloaded · version ${engine.installedVersion} · ${formatBytes(engine.installedBytes)} on disk`
}

export interface RowState {
	/** This row's download is running. */
	readonly downloading: boolean
	/** Some engine's download, removal or update is running. */
	readonly busy: boolean
}

export interface RowActions {
	readonly download: boolean
	readonly remove: boolean
	readonly cancel: boolean
	readonly update: boolean
}

export function engineActions(engine: EngineInfo, state: RowState): RowActions {
	if (state.downloading) return { download: false, remove: false, cancel: true, update: false }
	if (state.busy) return { download: false, remove: false, cancel: false, update: false }
	const installed = engine.installedVersion !== undefined
	return { download: !installed, remove: installed, cancel: false, update: installed && engine.id === 'ytdlp' }
}

/** What to tell the person after a download, or null when there is nothing to say. */
export function describeOutcome(outcome: EnsureOutcome): string | null {
	return outcome === 'cancelled' ? 'Cancelled. Nothing was kept.' : null
}
