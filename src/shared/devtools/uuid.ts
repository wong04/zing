// The UUID tool's generator (Krackn UtilitiesView.swift `UUIDTool`, which used
// Foundation's `UUID().uuidString`): random version-4 identifiers, upper-case
// like Foundation's; the tool offers a lower-case view.

export function makeUUID(): string {
	return crypto.randomUUID().toUpperCase()
}

/** The tool's "Generate 5". */
export function makeUUIDs(count: number): string[] {
	return Array.from({ length: count }, makeUUID)
}
