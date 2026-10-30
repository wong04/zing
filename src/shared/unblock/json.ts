/** A plain JSON object, as opposed to an array, null or a primitive. */
export function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === 'object' && value !== null && !Array.isArray(value)
}
