import './overview.css'
import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import type { OverviewState } from '@shared/ipc'
import { zing } from '../api'

/** The tab overview: a grid of page pictures. Choosing one switches to it; Escape closes. */
export function TabOverviewView() {
	const [state, setState] = useState<OverviewState | null>(null)
	const grid = useRef<HTMLDivElement>(null)

	useEffect(() => {
		const unsubscribe = zing.onOverview(setState)
		void zing.getOverview().then(setState)
		return unsubscribe
	}, [])
	const open = state !== null
	useEffect(() => {
		if (open) grid.current?.querySelector<HTMLElement>('[aria-current="true"]')?.focus()
	}, [open])

	if (!state) return null

	const choose = (id: string): void => {
		void zing.activateTab(id).then(() => zing.closeOverview())
	}
	const close = (id: string): void => {
		void zing.closeTab(id).then(() => zing.getOverview()).then(setState)
	}
	const onKeyDown = (e: KeyboardEvent): void => {
		if (e.key === 'Escape') void zing.closeOverview()
	}

	return (
		<div className="overview" onKeyDown={onKeyDown} onClick={() => void zing.closeOverview()}>
			<div ref={grid} className="overview-grid" role="list" aria-label="Open tabs" onClick={(e) => e.stopPropagation()}>
				{state.tabs.map((tab) => (
					<div key={tab.id} role="listitem" className="overview-cell">
						<button
							type="button"
							className="overview-card"
							aria-current={tab.active}
							aria-label={tab.title}
							onClick={() => choose(tab.id)}
						>
							<span className="overview-thumb">
								{tab.thumbnail ? <img src={tab.thumbnail} alt="" /> : <span className="overview-blank">{tab.title.slice(0, 1).toUpperCase()}</span>}
							</span>
							<span className="overview-caption">
								{tab.icon ? <img className="tab-favicon" src={tab.icon} alt="" /> : null}
								<span className="overview-title">{tab.title}</span>
							</span>
						</button>
						<button type="button" className="overview-close" aria-label={`Close ${tab.title}`} onClick={() => close(tab.id)}>
							×
						</button>
					</div>
				))}
			</div>
		</div>
	)
}
