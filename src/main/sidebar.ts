// Port of Krackn's `EdgeRevealMonitor` (App/Sidebar.swift). Pure: the caller
// samples the cursor on a timer. The reveal has to come from watching the
// cursor, not from a hover strip, because an invisible strip along the left
// edge would sit over the web view and eat its clicks (Krackn gotcha).
import { SIDEBAR_COLUMN_WIDTH } from '@shared/shell/shellState'

export interface CursorSample {
	/** Relative to the window's content area, in DIPs. */
	readonly x: number
	readonly y: number
	/** False once the cursor has left the content area. */
	readonly inWindow: boolean
	/** Milliseconds, any monotonic clock. */
	readonly now: number
}

export interface EdgeRevealOptions {
	readonly onChange: (revealed: boolean) => void
	/** How near the left edge counts as touching it. */
	readonly edge?: number
	/** A short dwell, so sweeping past the edge on the way to another window doesn't fling the sidebar out. */
	readonly dwellMs?: number
	/** Moving further than this from the left edge puts it away again. */
	readonly hideBeyond?: number
}

const DEFAULT_EDGE = 6
const DEFAULT_DWELL_MS = 70
const DEFAULT_PEEK_MS = 1400

export class EdgeReveal {
	readonly #onChange: (revealed: boolean) => void
	readonly #edge: number
	readonly #dwellMs: number
	readonly #hideBeyond: number
	#enabled = false
	#revealed = false
	#dwellStart: number | undefined
	#peekUntil: number | undefined

	constructor(options: EdgeRevealOptions) {
		this.#onChange = options.onChange
		this.#edge = options.edge ?? DEFAULT_EDGE
		this.#dwellMs = options.dwellMs ?? DEFAULT_DWELL_MS
		this.#hideBeyond = options.hideBeyond ?? SIDEBAR_COLUMN_WIDTH + 36
	}

	get revealed(): boolean {
		return this.#revealed
	}

	get enabled(): boolean {
		return this.#enabled
	}

	/** Enabled while the sidebar is unpinned; turning it off puts the sidebar away at once. */
	setEnabled(enabled: boolean): void {
		this.#enabled = enabled
		this.#dwellStart = undefined
		this.#peekUntil = undefined
		if (!enabled) this.#set(false)
	}

	/** Shows the sidebar briefly, as feedback that Unpin worked and where it went. */
	peek(now: number, durationMs = DEFAULT_PEEK_MS): void {
		if (!this.#enabled) return
		this.#peekUntil = now + durationMs
		this.#set(true)
	}

	update(sample: CursorSample): void {
		if (!this.#enabled) return
		if (this.#revealed) {
			this.#updateRevealed(sample)
			return
		}
		const atEdge = sample.inWindow && sample.x <= this.#edge && sample.y > 0
		if (!atEdge) {
			this.#dwellStart = undefined
			return
		}
		this.#dwellStart ??= sample.now
		if (sample.now - this.#dwellStart < this.#dwellMs) return
		this.#dwellStart = undefined
		this.#set(true)
	}

	#updateRevealed(sample: CursorSample): void {
		if (this.#peekUntil !== undefined && sample.now < this.#peekUntil) return
		this.#peekUntil = undefined
		if (sample.inWindow && sample.x <= this.#hideBeyond) return
		this.#set(false)
	}

	#set(revealed: boolean): void {
		if (this.#revealed === revealed) return
		this.#revealed = revealed
		this.#onChange(revealed)
	}
}
