import { DEFAULT_SETTINGS, RETIRED_SETTING_KEYS, SETTING_KEYS, acceptsValue, isSettingKey, type SettingKey, type Settings } from './schema'

export class InvalidSettingError extends Error {
	constructor(
		readonly key: string,
		readonly value: unknown,
	) {
		super(`Invalid value for setting "${key}": ${JSON.stringify(value)}`)
		this.name = 'InvalidSettingError'
	}
}

export class UnknownSettingError extends Error {
	constructor(readonly key: string) {
		super(`Unknown setting "${key}"`)
		this.name = 'UnknownSettingError'
	}
}

/**
 * Builds a full settings object from stored JSON. Keys that are absent take
 * their default; retired keys are skipped; an unknown key (from a newer or older
 * build sharing the file) is skipped and passed to `onUnknownKey`, so it can't
 * reset everything else. Known keys must be valid, or this throws rather than
 * quietly replacing what the user chose.
 */
export function parseSettings(stored: unknown, onUnknownKey: (key: string) => void): Settings {
	if (typeof stored !== 'object' || stored === null || Array.isArray(stored)) {
		throw new InvalidSettingError('(root)', stored)
	}
	const values: Record<string, unknown> = { ...DEFAULT_SETTINGS }
	for (const [key, value] of Object.entries(stored)) {
		if (RETIRED_SETTING_KEYS.includes(key)) continue
		if (!isSettingKey(key)) {
			onUnknownKey(key)
			continue
		}
		if (!acceptsValue(key, value)) throw new InvalidSettingError(key, value)
		values[key] = value
	}
	return values as unknown as Settings
}

/** Only what differs from the defaults, so a later default change reaches users who never touched it. */
export function changedSettings(settings: Settings): Partial<Settings> {
	const changed: Record<string, unknown> = {}
	for (const key of SETTING_KEYS) {
		if (JSON.stringify(settings[key]) !== JSON.stringify(DEFAULT_SETTINGS[key])) changed[key] = settings[key]
	}
	return changed as Partial<Settings>
}

export type SettingsListener = (settings: Settings, key: SettingKey) => void

export class SettingsStore {
	#values: Settings
	readonly #listeners = new Set<SettingsListener>()

	constructor(initial: Settings = DEFAULT_SETTINGS) {
		this.#values = initial
	}

	get<K extends SettingKey>(key: K): Settings[K] {
		return this.#values[key]
	}

	set<K extends SettingKey>(key: K, value: Settings[K]): void {
		if (!acceptsValue(key, value)) throw new InvalidSettingError(key, value)
		if (this.#values[key] === value) return
		this.#values = { ...this.#values, [key]: value }
		for (const listener of this.#listeners) listener(this.#values, key)
	}

	snapshot(): Settings {
		return this.#values
	}

	subscribe(listener: SettingsListener): () => void {
		this.#listeners.add(listener)
		return () => this.#listeners.delete(listener)
	}
}
