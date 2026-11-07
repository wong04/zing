import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { AccountPickerState } from '@shared/browser/pageUi'
import { followHostState } from '../hostState'
import { zing } from '../api'
import { useAccentTheme } from '../settings/useAccentTheme'
import './pageui.css'

/**
 * Root of the account-picker view: the vault's saved usernames for the focused sign-in field, drawn by Zing under
 * it, so the page never sees them. Usernames only. The view never takes the keyboard: the host reads the arrows,
 * Enter and Esc from real key presses on the page and sends the selection (`active`) here. A click chooses.
 */
export function AccountPicker() {
	useAccentTheme()
	const [picker, setPicker] = useState<AccountPickerState | null>(null)
	const root = useRef<HTMLDivElement>(null)
	useEffect(
		() =>
			followHostState(zing.onAccountPicker, zing.getAccountPicker, (state) => {
				// The bridge leaves out a null value, so a closed picker can arrive as undefined.
				setPicker(state ?? null)
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
	if (!picker) return null

	return (
		<div ref={root} className="picker">
			<div className="pageui-card picker-card">
				<span className="picker-title" id="picker-title">
					Saved logins
				</span>
				<ul
					className="picker-list"
					role="listbox"
					aria-labelledby="picker-title"
					aria-activedescendant={picker.active >= 0 ? `picker-entry-${picker.active}` : undefined}
				>
					{picker.entries.map((entry, index) => (
						<li
							key={entry.id}
							id={`picker-entry-${index}`}
							role="option"
							aria-selected={index === picker.active}
							className={index === picker.active ? 'picker-entry active' : 'picker-entry'}
							onClick={() => void zing.fillLogin(picker.pageId, entry.id)}
						>
							{entry.username === '' ? <span className="picker-blank">No username</span> : entry.username}
						</li>
					))}
				</ul>
			</div>
		</div>
	)
}
