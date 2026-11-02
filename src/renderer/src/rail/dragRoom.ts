/**
 * The room a held row leaves among the others: the distance from its top to the next row's top, so the
 * overlapping cards keep their spacing. The last row measures from the previous one; a lone row, its height.
 * Keys without a frame (rows not registered, such as split members) are left out. A held key without one
 * cannot be picked up, so it throws.
 */
export function dragRoom(
	keys: readonly string[],
	held: string,
	frames: ReadonlyMap<string, { readonly top: number; readonly height: number }>,
): number {
	const placed = keys.flatMap((key) => {
		const frame = frames.get(key)
		return frame ? [{ key, frame }] : []
	})
	const at = placed.findIndex((row) => row.key === held)
	const own = placed[at]
	if (!own) throw new RangeError(`dragRoom: held key "${held}" has no frame`)
	const next = placed[at + 1]
	if (next) return next.frame.top - own.frame.top
	const previous = placed[at - 1]
	if (previous) return own.frame.top - previous.frame.top
	return own.frame.height
}
