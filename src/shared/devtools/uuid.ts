// The UUID tool's generator: random version-4 identifiers, upper-case like
// Foundation's `UUID().uuidString`; the tool offers a lower-case view.

export function makeUUID(): string {
	return crypto.randomUUID().toUpperCase()
}

/** The tool's "Generate 5". */
export function makeUUIDs(count: number): string[] {
	return Array.from({ length: count }, makeUUID)
}
