import { useRef, useState, type KeyboardEvent } from 'react'
import { FAVOURITES_ID, PINNED_ID, isFolder, type BookmarkFolder, type BookmarkLink, type BookmarkNode } from '@shared/library/library'
import { zing } from '../api'
import { CloseIcon } from '../chrome/icons'
import { FolderIcon } from '../components/icons'
import { ribbonLayout } from '@shared/layout/ribbons'
import { AT_END } from './dropTarget'
import { FavouritesPanel } from './FavouritesPanel'
import { ItemIcon, itemMenu, open, type SavedFrame } from './savedItem'

const EMPTY_HINT = 'Drag a tab here, or press Ctrl+D'

/** The Favourites as ribbons hanging along the top of the rail: as many as the width fits, the rest behind "+N". Ctrl+D adds the page in front. */
export function FavouriteRibbons({ nodes, frame, innerWidth }: { nodes: readonly BookmarkNode[]; frame: SavedFrame; innerWidth: number }) {
	const { shown, more } = ribbonLayout(nodes.length, innerWidth)
	const [expanded, setExpanded] = useState(false)
	const moreButton = useRef<HTMLButtonElement>(null)
	// Focus is on a row in the panel when it closes from the keyboard: it goes back to "+N", not nowhere.
	const close = (): void => {
		if (document.activeElement?.closest('.rail-favourites-panel')) moreButton.current?.focus()
		setExpanded(false)
	}
	// Opening a favourite from its ribbon closes the panel too; a drag's release must not open or close anything.
	const activate = (id: string): void => {
		if (frame.endsDrag()) return
		setExpanded(false)
		void zing.openSavedTab(id)
	}
	const holdsActive = nodes.slice(shown).some((node) => {
		const tab = frame.tabsByItem.get(node.id)
		return tab !== undefined && tab.id === frame.activeId
	})
	return (
		<div className="rail-favourites" role="list" aria-label="Favourites" data-drop-folder={FAVOURITES_ID}>
			{nodes.length === 0 ? <div className="rail-ribbon empty" role="listitem" title={EMPTY_HINT} aria-label={EMPTY_HINT} /> : null}
			{nodes.slice(0, shown).map((node, index) => {
				const place = { 'data-drop-item': node.id, 'data-drop-parent': FAVOURITES_ID, 'data-drop-index': index, 'data-drop-axis': 'x' }
				if (isFolder(node)) {
					return (
						<div
							key={node.id}
							role="listitem"
							aria-label={node.title}
							title={node.title}
							className="rail-ribbon"
							{...place}
							{...frame.drag(node.id)}
							onClick={(e) => {
								if (!frame.endsDrag()) void zing.openFolderMenu(node.id, { x: e.clientX, y: e.clientY })
							}}
							onContextMenu={itemMenu(node.id)}
						>
							<FolderIcon size={16} />
						</div>
					)
				}
				const tab = frame.tabsByItem.get(node.id)
				const active = tab !== undefined && tab.id === frame.activeId
				return (
					<div
						key={node.id}
						role="listitem"
						aria-label={node.title}
						aria-current={active ? 'page' : undefined}
						title={node.title}
						data-item-id={node.id}
						className={['rail-ribbon', active && 'active', !tab && 'unloaded'].filter(Boolean).join(' ')}
						{...place}
						{...frame.drag(node.id)}
						onClick={() => activate(node.id)}
						onContextMenu={itemMenu(node.id)}
					>
						<ItemIcon link={node} tab={tab} />
					</div>
				)
			})}
			{more > 0 ? (
				<button
					ref={moreButton}
					type="button"
					className={['rail-ribbon', 'more', holdsActive && 'holds-active'].filter(Boolean).join(' ')}
					aria-label={`${more} more favourites`}
					aria-expanded={expanded}
					aria-controls="rail-favourites-panel"
					title={`${more} more favourites`}
					data-drop-item="more"
					data-drop-parent={FAVOURITES_ID}
					data-drop-index={shown}
					data-drop-axis="x"
					onClick={() => {
						if (!frame.endsDrag()) setExpanded(!expanded)
					}}
				>
					+{more}
				</button>
			) : null}
			{expanded && more > 0 ? <FavouritesPanel nodes={nodes.slice(shown)} firstIndex={shown} frame={frame} onClose={close} /> : null}
		</div>
	)
}

const CLOSED_KEY = 'zing.rail.closedFolders'

/** Which pinned folders are folded, remembered on this PC only (a convenience; nothing breaks without it). */
function useClosedFolders(): [ReadonlySet<string>, (id: string) => void] {
	const [closed, setClosed] = useState<ReadonlySet<string>>(() => {
		try {
			return new Set(JSON.parse(localStorage.getItem(CLOSED_KEY) ?? '[]') as string[])
		} catch {
			return new Set()
		}
	})
	const toggle = (id: string): void => {
		const next = new Set(closed)
		if (next.has(id)) next.delete(id)
		else next.add(id)
		setClosed(next)
		try {
			localStorage.setItem(CLOSED_KEY, JSON.stringify([...next]))
		} catch {
			// Storage can be off; the folders just start open next time.
		}
	}
	return [closed, toggle]
}

/** The pinned items, in folders that fold, like a bookmark bar on its side. */
export function PinnedSection({ nodes, frame }: { nodes: readonly BookmarkNode[]; frame: SavedFrame }) {
	const [closed, toggle] = useClosedFolders()
	const [renaming, setRenaming] = useState<string>()
	const newFolder = async (): Promise<void> => {
		const id = await zing.applyLibrary({ type: 'newFolder', ids: [] })
		if (id === undefined) return
		await zing.applyLibrary({ type: 'moveInto', id, parent: PINNED_ID, to: AT_END })
		setRenaming(id)
	}
	const rows = (list: readonly BookmarkNode[], parent: string, depth: number) =>
		list.map((node, index) => {
			const place = { 'data-drop-item': node.id, 'data-drop-parent': parent, 'data-drop-index': index, 'data-drop-axis': 'y' }
			if (isFolder(node)) {
				const open = !closed.has(node.id)
				return (
					<div key={node.id} className="rail-folder">
						<FolderRow folder={node} open={open} depth={depth} renaming={renaming === node.id} place={place} frame={frame} onToggle={() => toggle(node.id)} onRename={setRenaming} />
						{open ? rows(node.children, node.id, depth + 1) : null}
					</div>
				)
			}
			return <ItemRow key={node.id} link={node} depth={depth} renaming={renaming === node.id} place={place} frame={frame} onRename={setRenaming} />
		})
	return (
		<section className="rail-pinned-items" aria-label="Pinned" data-drop-folder={PINNED_ID}>
			<div className="rail-section-head">
				<span>Pinned</span>
				<button type="button" className="rail-icon-button" aria-label="New folder" title="New folder" onClick={() => void newFolder()}>
					<FolderIcon size={13} />
				</button>
			</div>
			{nodes.length === 0 ? <p className="rail-hint">Drag a tab here to pin it</p> : rows(nodes, PINNED_ID, 0)}
		</section>
	)
}

type Place = Record<string, string | number>

interface RowProps {
	readonly depth: number
	readonly renaming: boolean
	readonly place: Place
	readonly frame: SavedFrame
	readonly onRename: (id: string | undefined) => void
}

function FolderRow({ folder, open, depth, renaming, place, frame, onToggle, onRename }: RowProps & { folder: BookmarkFolder; open: boolean; onToggle: () => void }) {
	return (
		<div
			role="button"
			tabIndex={0}
			aria-expanded={open}
			aria-label={folder.title}
			className="rail-row rail-folder-head"
			style={{ paddingLeft: 9 + depth * 14 }}
			data-drop-folder={folder.id}
			{...place}
			{...frame.drag(folder.id)}
			onClick={() => {
				if (!frame.endsDrag()) onToggle()
			}}
			onKeyDown={(e) => {
				if (e.key === 'Enter' || e.key === ' ') onToggle()
			}}
			onDoubleClick={() => onRename(folder.id)}
			onContextMenu={itemMenu(folder.id)}
		>
			<span className="rail-row-icon">
				<FolderIcon size={15} />
			</span>
			<Title id={folder.id} title={folder.title} renaming={renaming} onRename={onRename} />
			<span className={open ? 'rail-chevron open' : 'rail-chevron'} aria-hidden="true">
				›
			</span>
		</div>
	)
}

function ItemRow({ link, depth, renaming, place, frame, onRename }: RowProps & { link: BookmarkLink }) {
	const tab = frame.tabsByItem.get(link.id)
	const active = tab !== undefined && tab.id === frame.activeId
	return (
		<div
			role="button"
			tabIndex={0}
			aria-label={link.title}
			aria-current={active ? 'page' : undefined}
			title={link.url}
			data-item-id={link.id}
			className={['rail-row', 'rail-item', active && 'active', !tab && 'unloaded'].filter(Boolean).join(' ')}
			style={{ paddingLeft: 9 + depth * 14 }}
			{...place}
			{...frame.drag(link.id)}
			onClick={() => open(frame, link.id)}
			onKeyDown={(e) => {
				if (e.key === 'Enter' || e.key === ' ') open(frame, link.id)
			}}
			onDoubleClick={() => onRename(link.id)}
			onContextMenu={itemMenu(link.id)}
		>
			<span className="rail-row-icon">
				<ItemIcon link={link} tab={tab} />
			</span>
			<Title id={link.id} title={link.title} renaming={renaming} onRename={onRename} />
			{tab ? (
				// Closing unloads the tab; the item stays.
				<button
					type="button"
					className="rail-close"
					aria-label={`Close ${link.title}`}
					title="Close Tab"
					onPointerDown={(e) => e.stopPropagation()}
					onClick={(e) => {
						e.stopPropagation()
						void zing.closeTab(tab.id)
					}}
				>
					<CloseIcon />
				</button>
			) : null}
		</div>
	)
}

/** A row's title, or the field that renames it (double-click). */
function Title({ id, title, renaming, onRename }: { id: string; title: string; renaming: boolean; onRename: (id: string | undefined) => void }) {
	if (!renaming) return <span className="rail-row-title">{title}</span>
	const commit = (value: string): void => {
		onRename(undefined)
		if (value.trim() && value !== title) void zing.applyLibrary({ type: 'rename', id, title: value })
	}
	return (
		<input
			className="rail-rename"
			aria-label="Name"
			defaultValue={title}
			autoFocus
			onPointerDown={(e) => e.stopPropagation()}
			onClick={(e) => e.stopPropagation()}
			onKeyDown={(e: KeyboardEvent<HTMLInputElement>) => {
				e.stopPropagation()
				if (e.key === 'Enter') commit(e.currentTarget.value)
				if (e.key === 'Escape') onRename(undefined)
			}}
			onBlur={(e) => commit(e.currentTarget.value)}
		/>
	)
}
