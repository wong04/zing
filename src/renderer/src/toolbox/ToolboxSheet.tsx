import { useEffect, useMemo, useState, type KeyboardEvent } from 'react'
import { itemsIn, searchEntries, toolboxGroups } from '@shared/toolbox/catalog'
import { panelContentFromId, panelId, panelTitle, type PanelContent } from '@shared/toolbox/panelContent'
import { rank } from '@shared/toolbox/toolboxSearch'
import { zing } from '../api'
import { CloseGlyph, IconButton, PillField } from '../components'
import { PanelIcon } from '../panels/panelIcon'
import './toolbox.css'

/** A tool opens where the sidebar's recent rows open it. */
function openTool(content: PanelContent): void {
	void zing.openPanel(panelId(content), { x: window.screenX + 560, y: window.screenY + 160 })
}

function Item({ content, className, testId, selected }: { content: PanelContent; className: string; testId: string; selected?: boolean }) {
	return (
		<button
			type="button"
			className={selected ? `${className} selected` : className}
			aria-current={selected ? 'true' : undefined}
			data-testid={testId}
			onClick={() => openTool(content)}
		>
			<span className="toolbox-item-icon">
				<PanelIcon content={panelId(content)} size={18} />
			</span>
			<span className="toolbox-item-title">{panelTitle(content)}</span>
		</button>
	)
}

function Results({ results, selected }: { results: readonly PanelContent[]; selected: number }) {
	if (results.length === 0) return <p className="toolbox-none">Nothing matches that. Try what you want to do, such as checksum or unblock.</p>
	return (
		<div className="toolbox-results">
			{results.map((content, index) => (
				<Item key={panelId(content)} content={content} className="toolbox-result" testId="toolbox-result" selected={index === selected} />
			))}
		</div>
	)
}

/** The Toolbox: every tool under the job it does, or found by what you type. Return opens the best match. */
export function ToolboxSheet() {
	const [query, setQuery] = useState('')
	const [selected, setSelected] = useState(0)
	const [shown, setShown] = useState(false)
	const entries = useMemo(() => searchEntries(), [])
	const searching = query.trim().length > 0
	const results = useMemo(
		() =>
			rank(query, entries).map((entry) => {
				const content = panelContentFromId(entry.id)
				if (!content) throw new Error(`The Toolbox catalog lists ${entry.id}, which names nothing`)
				return content
			}),
		[query, entries],
	)

	useEffect(() => {
		const frame = requestAnimationFrame(() => setShown(true))
		return () => cancelAnimationFrame(frame)
	}, [])

	const close = (): void => void zing.closeToolbox()
	// Escape works from anywhere in the sheet, so it lives on the backdrop.
	const onSheetKeyDown = (e: KeyboardEvent): void => {
		if (e.key !== 'Escape') return
		e.preventDefault()
		close()
	}
	// Arrows and Enter belong to the search field alone: on Close or a result, Enter must press that button.
	const onSearchKeyDown = (e: KeyboardEvent): void => {
		if (!searching) return
		if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
			e.preventDefault()
			const delta = e.key === 'ArrowDown' ? 1 : -1
			setSelected((index) => Math.min(Math.max(index + delta, 0), results.length - 1))
			return
		}
		if (e.key !== 'Enter') return
		const pick = results[selected]
		if (!pick) return
		e.preventDefault()
		openTool(pick)
	}

	return (
		<div className={shown ? 'sheet-backdrop shown' : 'sheet-backdrop'} onKeyDown={onSheetKeyDown}>
			<div className="sheet-dismiss" onPointerDown={close} aria-hidden="true" />
			<div className="sheet toolbox-sheet" role="dialog" aria-modal="true" aria-label="Toolbox" tabIndex={-1}>
				<header className="toolbox-head">
					<h2>Toolbox</h2>
					<PillField
						type="search"
						aria-label="Search tools"
						placeholder="Search tools: try checksum, unblock or json"
						autoFocus
						onKeyDown={onSearchKeyDown}
						value={query}
						onChange={(value) => {
							setQuery(value)
							setSelected(0)
						}}
					/>
					<IconButton aria-label="Close Toolbox (Esc)" size={28} onClick={close}>
						<CloseGlyph size={14} />
					</IconButton>
				</header>
				<div className="toolbox-body">
					{searching ? (
						<Results results={results} selected={selected} />
					) : (
						toolboxGroups.map((group) => (
							<section key={group} className="toolbox-group" aria-label={group}>
								<h3>{group}</h3>
								<div className="toolbox-tiles">
									{itemsIn(group).map((content) => (
										<Item key={panelId(content)} content={content} className="toolbox-tile" testId="toolbox-tile" />
									))}
								</div>
							</section>
						))
					)}
				</div>
			</div>
		</div>
	)
}
