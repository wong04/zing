import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import type { AccountPickerState } from '@shared/browser/pageUi'
import { followHostState } from '../hostState'
import { zing } from '../api'
import { useAccentTheme } from '../settings/useAccentTheme'
import './pageui.css'

/**
 * Root of the account-picker view: the vault's saved usernames for the focused sign-in field, drawn by Zing under
 * it, so the page never sees them. Usernames only. Arrow keys move, Enter chooses, Esc closes; the host moves the
 * keyboard here when the down arrow is pressed in the field.
 */
export function AccountPicker() {
	useAccentTheme()
	const [picker, setPicker] = useState<AccountPickerState | null>(null)
	const [active, setActive] = useState(0)
	const root = useRef<HTMLDivElement>(null)
	const list = useRef<HTMLUListElement>(null)
	useEffect(
		() =>
			followHostState(zing.onAccountPicker, zing.getAccountPicker, (state) => {
				// The bridge leaves out a null value, so a closed picker can arrive as undefined.
				setPicker(state ?? null)
				setActive(0)
			}),
		[],
	)
	const open = picker !== null
	useLayoutEffect(() => {
		const element = root.current
		if (!element || !open) return
		const report = (): void => void zing.setAccountPickerHeight(element.getBoundingClientRect().height)
		report()
		const observer = new ResizeObserver(report)
		observer.observe(element)
		return () => observer.disconnect()
	}, [open])
	// Ready for the keys whenever the host hands this view the keyboard.
	useEffect(() => {
		list.current?.focus()
	}, [picker])
	if (!picker) return null

	const entries = picker.entries
	const choose = (index: number): void => {
		const entry = entries[index]
		if (entry) void zing.fillLogin(picker.pageId, entry.id)
	}
	const onKeyDown = (e: KeyboardEvent<HTMLUListElement>): void => {
		if (e.key === 'ArrowDown') setActive((i) => (i + 1) % entries.length)
		else if (e.key === 'ArrowUp') setActive((i) => (i - 1 + entries.length) % entries.length)
		else if (e.key === 'Home') setActive(0)
		else if (e.key === 'End') setActive(entries.length - 1)
		else if (e.key === 'Enter') choose(active)
		else if (e.key === 'Escape') void zing.closeAccountPicker()
		else return
		e.preventDefault()
	}
	return (
		<div ref={root} className="picker">
			<div className="pageui-card picker-card">
				<span className="picker-title" id="picker-title">
					Saved logins
				</span>
				<ul
					ref={list}
					className="picker-list"
					role="listbox"
					aria-labelledby="picker-title"
					tabIndex={0}
					aria-activedescendant={`picker-entry-${active}`}
					onKeyDown={onKeyDown}
				>
					{entries.map((entry, index) => (
						<li
							key={entry.id}
							id={`picker-entry-${index}`}
							role="option"
							aria-selected={index === active}
							className={index === active ? 'picker-entry active' : 'picker-entry'}
							onMouseEnter={() => setActive(index)}
							onClick={() => choose(index)}
						>
							{entry.username === '' ? <span className="picker-blank">No username</span> : entry.username}
						</li>
					))}
				</ul>
			</div>
		</div>
	)
}
