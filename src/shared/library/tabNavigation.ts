// The arithmetic behind the tab shortcuts. Ported from Krackn's BrowserLibrary.swift.

// Ctrl+Tab: the next tab, wrapping from the last to the first.
export function nextTab(after: number, count: number): number | undefined {
	if (count <= 0) return undefined
	return (after + 1) % count
}

// Ctrl+Shift+Tab: the previous tab, wrapping from the first to the last.
export function previousTab(before: number, count: number): number | undefined {
	if (count <= 0) return undefined
	return (before - 1 + count) % count
}

// Ctrl+1–8 pick that tab if it exists; Ctrl+9 always means the last one.
export function tabForShortcut(number: number, count: number): number | undefined {
	if (count <= 0 || number < 1 || number > 9) return undefined
	if (number === 9) return count - 1
	return number <= count ? number - 1 : undefined
}

// Recently closed tabs, newest on top, for Ctrl+Shift+T. Memory only: a list
// of closed pages on disk would be exactly the history this browser doesn't keep.
export class ClosedStack<T> {
	#items: T[] = []

	constructor(readonly capacity = 25) {}

	get items(): readonly T[] {
		return this.#items
	}

	get isEmpty(): boolean {
		return this.#items.length === 0
	}

	push(item: T): void {
		this.#items.push(item)
		if (this.#items.length > this.capacity) this.#items.splice(0, this.#items.length - this.capacity)
	}

	pop(): T | undefined {
		return this.#items.pop()
	}
}
