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

export interface YtDlpUpdate {
	readonly version: string
	readonly changed: boolean
}

export function describeEngine(engine: EngineInfo): string {
	if (engine.installedVersion === undefined) return `Not downloaded · ${formatBytes(engine.downloadBytes)} download`
	return `Downloaded · version ${engine.installedVersion} · ${formatBytes(engine.installedBytes)} on disk`
}
