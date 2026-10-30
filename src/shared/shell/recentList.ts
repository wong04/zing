// Port of Krackn App/PanelLayout.swift (`RecentList`).
//
// The sidebar's "recently used" list: most recent first, no duplicates,
// capped. Stored as identifier strings so it survives relaunch without
// recording anything more revealing than which tool was opened.

export const recentCapacity = 3

export function touching(id: string, list: readonly string[], capacity = recentCapacity): string[] {
	return [id, ...list.filter((item) => item !== id)].slice(0, capacity)
}

export function removing(id: string, list: readonly string[]): string[] {
	return list.filter((item) => item !== id)
}
