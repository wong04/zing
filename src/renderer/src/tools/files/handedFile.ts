import type { PickedFile } from '@shared/engines/engineJob'

/** The file the host handed the panel (Windows' Open with), as JSON: a token it granted this window, and the file's name. */
export function handedFile(value: string): PickedFile {
	const parsed: unknown = JSON.parse(value)
	if (typeof parsed !== 'object' || parsed === null) throw new TypeError(`The viewer was handed ${value}, not a file`)
	const { token, name } = parsed as Record<string, unknown>
	if (typeof token !== 'string' || typeof name !== 'string') throw new TypeError(`The viewer was handed ${value}, not a file`)
	return { token, name }
}
