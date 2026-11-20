import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { AddressSuggestionKind, AddressSuggestionsState } from '@shared/browser/pageUi'
import { followHostState } from '../hostState'
import { zing } from '../api'
import { useAccentTheme } from '../settings/useAccentTheme'
import './pageui.css'

const KIND_LABELS: Record<AddressSuggestionKind, string> = { tab: 'Switch to tab', bookmark: 'Bookmark', reading: 'Reading List' }

/**
 * Root of the address-suggestions view: open tabs, bookmarks and Reading List items matching what's typed, drawn
 * by Zing under the address field. The view never takes the keyboard: the field keeps it and sends the arrows'
 * highlight (`active`) through the host. A click picks a row.
 */
export function AddressSuggestions() {
	useAccentTheme()
	const [state, setState] = useState<AddressSuggestionsState | null>(null)
	const root = useRef<HTMLDivElement>(null)
	useEffect(
		() =>
			followHostState(zing.onAddressSuggestions, zing.getAddressSuggestions, (next) => {
				// The bridge leaves out a null value, so a closed list can arrive as undefined.
				setState(next ?? null)
			}),
		[],
	)
	const open = state !== null
	useLayoutEffect(() => {
		const element = root.current
		if (!element || !open) return
		const report = (): void => void zing.setAddressSuggestionsHeight(element.getBoundingClientRect().height)
		report()
		const observer = new ResizeObserver(report)
		observer.observe(element)
		return () => observer.disconnect()
	}, [open])
	if (!state) return null

	return (
		<div ref={root} className="suggest">
			<ul className="pageui-card suggest-list" role="listbox" aria-label="Suggestions" aria-activedescendant={state.active >= 0 ? `suggest-row-${state.active}` : undefined}>
				{state.items.map((item, index) => (
					<li
						key={`${item.kind}:${item.tabId ?? item.url}`}
						id={`suggest-row-${index}`}
						role="option"
						aria-selected={index === state.active}
						className={index === state.active ? 'suggest-row active' : 'suggest-row'}
						// Before the field's blur closes the list.
						onMouseDown={(e) => {
							e.preventDefault()
							void zing.openAddressSuggestion(item)
						}}
					>
						<span className="suggest-title">{item.title || item.url}</span>
						<span className="suggest-url">{item.url}</span>
						<span className="suggest-kind">{KIND_LABELS[item.kind]}</span>
					</li>
				))}
			</ul>
		</div>
	)
}
