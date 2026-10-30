import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import type { TabView } from '@shared/ipc'
import { zing } from '../api'
import { addressText } from './display'

interface Draft {
	readonly tabId: string
	readonly text: string
}

export function AddressBar({ tab }: { tab: TabView | undefined }) {
	const input = useRef<HTMLInputElement>(null)
	// A draft belongs to the tab it was typed in; switching tabs shows that tab's address.
	const [draft, setDraft] = useState<Draft | null>(null)
	const justFocused = useRef(false)

	useEffect(
		() =>
			zing.onFocusAddress(() => {
				input.current?.focus()
				input.current?.select()
			}),
		[],
	)

	const value = draft && draft.tabId === tab?.id ? draft.text : addressText(tab?.url)

	const submit = (): void => {
		if (!draft || !tab) return
		setDraft(null)
		void zing.navigateTab(tab.id, draft.text)
	}

	const onKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
		if (e.key === 'Enter') submit()
		if (e.key !== 'Escape') return
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
			onChange={(e) => tab && setDraft({ tabId: tab.id, text: e.target.value })}
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
			onBlur={() => setDraft(null)}
		/>
	)
}
