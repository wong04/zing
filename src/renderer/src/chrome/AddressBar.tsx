import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import type { AddressSuggestion } from '@shared/browser/pageUi'
import type { TabView } from '@shared/ipc'
import { zing } from '../api'
import { addressText } from './display'
import { moveHighlight, suggestionRequests } from './suggestions'

interface Draft {
	readonly tabId: string
	readonly text: string
}

export function AddressBar({ tab }: { tab: TabView | undefined }) {
	const input = useRef<HTMLInputElement>(null)
	// A draft belongs to the tab it was typed in; switching tabs shows that tab's address.
	const [draft, setDraft] = useState<Draft | null>(null)
	const justFocused = useRef(false)
	// The rows Zing's own view draws under the field (#suggest); the field keeps the keyboard and moves the highlight.
	const [suggestions, setSuggestions] = useState<readonly AddressSuggestion[]>([])
	const [active, setActive] = useState(-1)
	const [requests] = useState(() =>
		suggestionRequests(
			(text, field) => zing.suggestAddresses(text, field),
			(items) => {
				setSuggestions(items)
				setActive(-1)
			},
		),
	)

	useEffect(
		() =>
			zing.onFocusAddress(() => {
				input.current?.focus()
				input.current?.select()
			}),
		[],
	)

	const value = draft && draft.tabId === tab?.id ? draft.text : addressText(tab?.url)

	useEffect(() => () => requests.cancel(), [requests])

	const closeSuggestions = (): void => {
		requests.cancel()
		setSuggestions([])
		setActive(-1)
		void zing.closeAddressSuggestions()
	}

	const submit = (): void => {
		if (!draft || !tab) return
		const picked = suggestions[active]
		closeSuggestions()
		setDraft(null)
		if (picked) void zing.openAddressSuggestion(picked)
		else void zing.navigateTab(tab.id, draft.text)
	}

	const onKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
		if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && suggestions.length > 0) {
			e.preventDefault()
			const next = moveHighlight(active, suggestions.length, e.key)
			setActive(next)
			void zing.highlightAddressSuggestion(next)
		}
		if (e.key === 'Enter') submit()
		if (e.key !== 'Escape') return
		// The first Esc only closes the list; the next one gives up what was typed.
		if (suggestions.length > 0) {
			closeSuggestions()
			return
		}
		setDraft(null)
		e.currentTarget.blur()
	}

	return (
		<input
			ref={input}
			className="address"
			type="text"
			spellCheck={false}
			autoComplete="off"
			aria-label="Address and search bar"
			placeholder="Search or enter address"
			value={value}
			onChange={(e) => {
				if (!tab) return
				setDraft({ tabId: tab.id, text: e.target.value })
				const { x, y, width, height } = e.target.getBoundingClientRect()
				requests.request(e.target.value, { x, y, width, height })
			}}
			onKeyDown={onKeyDown}
			onFocus={(e) => {
				justFocused.current = true
				e.currentTarget.select()
			}}
			// The click that focuses the field would otherwise collapse the selection to a caret.
			onMouseUp={(e) => {
				if (!justFocused.current) return
				justFocused.current = false
				e.preventDefault()
			}}
			onBlur={() => {
				setDraft(null)
				closeSuggestions()
			}}
		/>
	)
}
