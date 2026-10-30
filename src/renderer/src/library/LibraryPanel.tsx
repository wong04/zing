import { useEffect, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react'
import { CHROME_LAYOUT, LIBRARY_PANEL_WIDTH, type LibraryPanel as PanelKind } from '@shared/ipc'
import { FAVOURITES_ID, isFolder, visibleRows, type BookmarkNode, type BrowserLibrary } from '@shared/library/library'
import { zing } from '../api'

interface Row {
	readonly id: string
	readonly title: string
	readonly detail?: string
	readonly depth: number
	readonly folder: boolean
	readonly read?: boolean
}

function bookmarkRows(library: BrowserLibrary, expanded: ReadonlySet<string>): Row[] {
	return visibleRows(library.bookmarks, expanded).map(({ node, depth }) => ({
		id: node.id,
		title: node.title,
		...(isFolder(node) ? {} : { detail: node.url }),
		depth,
		folder: isFolder(node),
	}))
}

function readingRows(library: BrowserLibrary): Row[] {
	return library.readingList.map((item) => ({ id: item.id, title: item.title, detail: item.url, depth: 0, folder: false, read: item.isRead }))
}

/** The sibling list holding `id`, so a reorder knows its position among them. */
function siblingsOf(nodes: BookmarkNode[], id: string): BookmarkNode[] | undefined {
	if (nodes.some((n) => n.id === id)) return nodes
	for (const node of nodes) {
		if (!isFolder(node)) continue
		const hit = siblingsOf(node.children, id)
		if (hit) return hit
	}
	return undefined
}

function positionOf(library: BrowserLibrary, panel: PanelKind, id: string): { index: number; count: number } | undefined {
	const list: readonly { id: string }[] | undefined = panel === 'bookmarks' ? siblingsOf(library.bookmarks, id) : library.readingList
	const index = list?.findIndex((n) => n.id === id) ?? -1
	return list && index >= 0 ? { index, count: list.length } : undefined
}

interface LibraryPanelProps {
	readonly library: BrowserLibrary
	readonly panel: PanelKind
	readonly favouritesBar: boolean
	readonly offset: number
}

export function LibraryPanel({ library, panel, favouritesBar, offset }: LibraryPanelProps) {
	const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set([FAVOURITES_ID]))
	const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())
	const [focused, setFocused] = useState<string | undefined>()
	const [renaming, setRenaming] = useState<string | undefined>()
	const [dragged, setDragged] = useState<string | undefined>()
	const list = useRef<HTMLDivElement>(null)

	const rows = panel === 'bookmarks' ? bookmarkRows(library, expanded) : readingRows(library)
	const top = CHROME_LAYOUT.tabStripHeight + CHROME_LAYOUT.toolbarHeight + (favouritesBar ? CHROME_LAYOUT.favouritesBarHeight : 0)
	const style: CSSProperties = { top, left: offset, width: LIBRARY_PANEL_WIDTH }

	useEffect(() => list.current?.focus(), [panel])

	const select = (id: string, additive: boolean): void => {
		setFocused(id)
		setSelected((current) => {
			if (!additive) return new Set([id])
			const next = new Set(current)
			if (!next.delete(id)) next.add(id)
			return next
		})
	}
	const toggle = (id: string): void =>
		setExpanded((current) => {
			const next = new Set(current)
			if (!next.delete(id)) next.add(id)
			return next
		})
	const open = (row: Row): void => {
		if (row.folder) {
			toggle(row.id)
			return
		}
		void zing.openLibraryItem(row.id, 'current')
		if (panel === 'readingList') void zing.applyLibrary({ type: 'setRead', id: row.id, read: true })
	}
	const targets = (id: string): string[] => (selected.has(id) ? [...selected] : [id])
	const remove = (ids: string[]): void => void zing.applyLibrary({ type: 'remove', ids: ids.filter((id) => id !== FAVOURITES_ID) })
	const newFolder = async (): Promise<void> => {
		const id = await zing.applyLibrary({ type: 'newFolder', ids: [...selected].filter((s) => s !== FAVOURITES_ID) })
		if (!id) return
		setExpanded((current) => new Set(current).add(id))
		setSelected(new Set([id]))
		setFocused(id)
		setRenaming(id)
	}
	const commitRename = (id: string, title: string): void => {
		setRenaming(undefined)
		if (title.trim()) void zing.applyLibrary({ type: 'rename', id, title })
		list.current?.focus()
	}
	const reorder = (id: string, delta: -1 | 1): void => {
		const at = positionOf(library, panel, id)
		if (!at) return
		const target = at.index + delta
		if (target < 0 || target >= at.count) return
		void zing.applyLibrary({ type: 'move', id, to: delta < 0 ? target : target + 1 })
	}
	const dropOnRow = (target: Row): void => {
		if (!dragged || dragged === target.id) return
		const to = positionOf(library, panel, target.id)
		const id = dragged
		setDragged(undefined)
		if (!to) return
		void zing.applyLibrary({ type: 'move', id, to: to.index })
	}

	const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
		if (renaming) return
		const at = rows.findIndex((r) => r.id === focused)
		const row = rows[at]
		if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
			e.preventDefault()
			const delta = e.key === 'ArrowDown' ? 1 : -1
			if (e.altKey && row) {
				reorder(row.id, delta)
				return
			}
			const next = rows[Math.min(rows.length - 1, Math.max(0, at < 0 ? 0 : at + delta))]
			if (next) select(next.id, false)
			return
		}
		if (!row) return
		if (e.key === 'ArrowRight' && row.folder && !expanded.has(row.id)) toggle(row.id)
		else if (e.key === 'ArrowLeft' && row.folder && expanded.has(row.id)) toggle(row.id)
		else if (e.key === 'Enter') open(row)
		else if (e.key === 'Delete') remove(targets(row.id))
		else if (e.key === 'F2' && panel === 'bookmarks') setRenaming(row.id)
		else return
		e.preventDefault()
	}

	return (
		<aside className="library-panel" style={style} aria-label={panel === 'bookmarks' ? 'Bookmarks' : 'Reading List'}>
			<header className="library-header">
				<div className="library-tabs" role="tablist">
					<button type="button" role="tab" aria-selected={panel === 'bookmarks'} onClick={() => void zing.setLibraryPanel('bookmarks')}>
						Bookmarks
					</button>
					<button type="button" role="tab" aria-selected={panel === 'readingList'} onClick={() => void zing.setLibraryPanel('readingList')}>
						Reading List
					</button>
				</div>
				<button type="button" className="icon-button" aria-label="Close panel" title="Close panel" onClick={() => void zing.setLibraryPanel(null)}>
					×
				</button>
			</header>
			{panel === 'bookmarks' ? (
				<div className="library-actions">
					<button type="button" onClick={() => void newFolder()}>
						{selected.size > 0 ? 'New Folder from Selection' : 'New Folder'}
					</button>
				</div>
			) : null}
			<div ref={list} className="library-list" role="tree" tabIndex={0} onKeyDown={onKeyDown}>
				{rows.length === 0 ? <p className="library-empty">{panel === 'bookmarks' ? 'No bookmarks yet.' : 'Your Reading List is empty.'}</p> : null}
				{rows.map((row) => (
					<div
						key={row.id}
						role="treeitem"
						aria-selected={selected.has(row.id)}
						aria-expanded={row.folder ? expanded.has(row.id) : undefined}
						className={['library-row', selected.has(row.id) && 'selected', focused === row.id && 'focused', row.read && 'read'].filter(Boolean).join(' ')}
						style={{ paddingLeft: 8 + row.depth * 16 }}
						draggable={row.id !== FAVOURITES_ID}
						onDragStart={() => setDragged(row.id)}
						onDragOver={(e) => dragged && e.preventDefault()}
						onDrop={() => dropOnRow(row)}
						onClick={(e) => select(row.id, e.ctrlKey || e.metaKey)}
						onDoubleClick={() => open(row)}
					>
						<span className="library-glyph" aria-hidden="true">
							{row.folder ? (expanded.has(row.id) ? '▾' : '▸') : panel === 'readingList' && !row.read ? '●' : '·'}
						</span>
						{renaming === row.id ? (
							<input
								className="library-rename"
								autoFocus
								defaultValue={row.title}
								aria-label="Name"
								onFocus={(e) => e.currentTarget.select()}
								onClick={(e) => e.stopPropagation()}
								onKeyDown={(e) => {
									e.stopPropagation()
									if (e.key === 'Enter') commitRename(row.id, e.currentTarget.value)
									if (e.key === 'Escape') setRenaming(undefined)
								}}
								onBlur={(e) => commitRename(row.id, e.currentTarget.value)}
							/>
						) : (
							<span className="library-title" title={row.detail}>
								{row.title}
							</span>
						)}
						<span className="library-row-actions">
							{panel === 'bookmarks' ? (
								<button
									type="button"
									aria-label={`Rename ${row.title}`}
									title="Rename"
									onClick={(e) => {
										e.stopPropagation()
										setRenaming(row.id)
									}}
								>
									✎
								</button>
							) : (
								<button
									type="button"
									aria-label={row.read ? 'Mark unread' : 'Mark read'}
									title={row.read ? 'Mark unread' : 'Mark read'}
									onClick={(e) => {
										e.stopPropagation()
										void zing.applyLibrary({ type: 'setRead', id: row.id, read: !row.read })
									}}
								>
									○
								</button>
							)}
							{row.id === FAVOURITES_ID ? null : (
								<button
									type="button"
									aria-label={`Delete ${row.title}`}
									title="Delete"
									onClick={(e) => {
										e.stopPropagation()
										remove([row.id])
									}}
								>
									×
								</button>
							)}
						</span>
					</div>
				))}
			</div>
		</aside>
	)
}
