import { mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'
import { DEFAULT_SETTINGS, parseSettings, type Settings } from '@shared/settings'

export class SettingsFileError extends Error {
	constructor(
		readonly path: string,
		options: { cause: unknown },
	) {
		super(`Could not read settings from ${path}: ${(options.cause as Error).message}`, options)
		this.name = 'SettingsFileError'
	}
}

function isMissingFile(error: unknown): boolean {
	return (error as NodeJS.ErrnoException).code === 'ENOENT'
}

/** No file yet means first run: every key reads as its default. Anything unreadable is an error. */
export function loadSettingsFile(path: string): Settings {
	let raw: string
	try {
		raw = readFileSync(path, 'utf8')
	} catch (error) {
		if (isMissingFile(error)) return DEFAULT_SETTINGS
		throw new SettingsFileError(path, { cause: error })
	}
	try {
		return parseSettings(JSON.parse(raw))
	} catch (error) {
		throw new SettingsFileError(path, { cause: error })
	}
}

/** Temp file + rename, so a crash mid-write leaves the old file intact instead of a torn one. */
export function writeJsonAtomic(path: string, data: unknown): void {
	mkdirSync(dirname(path), { recursive: true })
	const temp = `${path}.${process.pid}.tmp`
	writeFileSync(temp, `${JSON.stringify(data, null, '\t')}\n`, 'utf8')
	renameSync(temp, path)
}

/** Debounces writes so dragging a slider is one write, not one per step. */
export class DebouncedJsonWriter {
	#pending: { readonly data: unknown } | undefined
	#timer: ReturnType<typeof setTimeout> | undefined

	constructor(
		readonly path: string,
		private readonly delayMs = 300,
	) {}

	schedule(data: unknown): void {
		this.#pending = { data }
		clearTimeout(this.#timer)
		this.#timer = setTimeout(() => this.flush(), this.delayMs)
	}

	/** Writes now if anything is pending. Called on quit so the last change isn't lost. */
	flush(): void {
		clearTimeout(this.#timer)
		this.#timer = undefined
		const pending = this.#pending
		if (!pending) return
		this.#pending = undefined
		writeJsonAtomic(this.path, pending.data)
	}
}
