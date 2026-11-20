import type { AddressSuggestion, FieldRect } from '@shared/browser/pageUi'

/** How long typing must pause before the host is asked again. */
export const SUGGEST_DELAY_MS = 50

/** The row the arrow key moves to. The rows wrap through -1, what was typed, as in other browsers. */
export function moveHighlight(active: number, count: number, key: 'ArrowDown' | 'ArrowUp'): number {
	if (count === 0) return -1
	if (key === 'ArrowDown') return active + 1 >= count ? -1 : active + 1
	return active <= -1 ? count - 1 : active - 1
}

export interface SuggestionRequests {
	/** Asks for this text, to show under this field, once typing has paused for the delay, replacing any request still waiting. */
	request(text: string, field: FieldRect): void
	/** Drops the waiting request, and the answer of any already sent. */
	cancel(): void
}

/**
 * Debounces the address bar's suggestion requests. Only the latest request's answer is applied: an answer to one
 * that was replaced or cancelled arrives too late to show.
 */
export function suggestionRequests(fetch: (text: string, field: FieldRect) => Promise<readonly AddressSuggestion[]>, apply: (items: readonly AddressSuggestion[]) => void, delay = SUGGEST_DELAY_MS): SuggestionRequests {
	let timer: ReturnType<typeof setTimeout> | undefined
	let latest = 0
	const cancel = (): void => {
		clearTimeout(timer)
		timer = undefined
		latest++
	}
	return {
		request(text, field) {
			cancel()
			const mine = latest
			timer = setTimeout(() => {
				timer = undefined
				void fetch(text, field).then((items) => {
					if (mine === latest) apply(items)
				})
			}, delay)
		},
		cancel,
	}
}
