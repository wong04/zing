import { useEffect } from 'react'
import { FAVOURITES_ID, isFolder, type BookmarkNode } from '@shared/library/library'
import { zing } from '../api'
import { FolderIcon } from '../components/icons'
import { ItemIcon, itemMenu, type SavedFrame } from './savedItem'

/** The favourites behind "+N": a list under the ribbons, opened by a click and dragged back among them. */
export function FavouritesPanel({ nodes, firstIndex, frame, onClose }: { nodes: readonly BookmarkNode[]; firstIndex: number; frame: SavedFrame; onClose: () => void }) {
	useEffect(() => {
		// A drag can blur the window, and the panel must stay open for it to land.
		const onBlur = (): void => {
			if (!document.querySelector('.rail-favourites .dragging')) onClose()
		}
		const onPointerDown = (e: PointerEvent): void => {
			if (!(e.target instanceof Element)) return
			// The ribbons are the panel's own bar: a drag from one must not close it.
			if (e.target.closest('.rail-favourites')) return
			onClose()
		}
		const onKeyDown = (e: KeyboardEvent): void => {
			if (e.key === 'Escape') onClose()
		}
		window.addEventListener('blur', onBlur)
		document.addEventListener('pointerdown', onPointerDown)
		window.addEventListener('keydown', onKeyDown)
		return () => {
			window.removeEventListener('blur', onBlur)
			document.removeEventListener('pointerdown', onPointerDown)
			window.removeEventListener('keydown', onKeyDown)
		}
	}, [onClose])

	return (
		<div id="rail-favourites-panel" className="rail-favourites-panel" role="list" aria-label="More favourites" data-drop-folder={FAVOURITES_ID}>
			<div className="rail-panel-head">{nodes.length} more favourites</div>
			{nodes.map((node, i) => (
				<PanelRow key={node.id} node={node} index={firstIndex + i} frame={frame} onClose={onClose} />
			))}
		</div>
	)
}

interface RowProps {
	readonly node: BookmarkNode
	readonly index: number
	readonly frame: SavedFrame
	readonly onClose: () => void
}

function PanelRow({ node, index, frame, onClose }: RowProps) {
	const place = { 'data-drop-item': node.id, 'data-drop-parent': FAVOURITES_ID, 'data-drop-index': index, 'data-drop-axis': 'y' }
	if (isFolder(node)) {
		return (
			<div
				role="listitem"
				tabIndex={0}
				aria-label={node.title}
				title={node.title}
				className="rail-row rail-panel-row"
				{...place}
				{...frame.drag(node.id)}
				onClick={(e) => {
					if (!frame.endsDrag()) void zing.openFolderMenu(node.id, { x: e.clientX, y: e.clientY })
				}}
				onKeyDown={(e) => {
					if (e.key !== 'Enter' && e.key !== ' ') return
					const rect = e.currentTarget.getBoundingClientRect()
					void zing.openFolderMenu(node.id, { x: rect.left, y: rect.bottom })
				}}
				onContextMenu={itemMenu(node.id)}
			>
				<span className="rail-row-icon">
					<FolderIcon size={15} />
				</span>
				<span className="rail-row-title">{node.title}</span>
			</div>
		)
	}
	const tab = frame.tabsByItem.get(node.id)
	const active = tab !== undefined && tab.id === frame.activeId
	const openItem = (): void => {
		onClose()
		void zing.openSavedTab(node.id)
	}
	return (
		<div
			role="listitem"
			tabIndex={0}
			aria-label={node.title}
			aria-current={active ? 'page' : undefined}
			title={node.title}
			data-item-id={node.id}
			className={['rail-row', 'rail-item', 'rail-panel-row', active && 'active', !tab && 'unloaded'].filter(Boolean).join(' ')}
			{...place}
			{...frame.drag(node.id)}
			onClick={() => {
				if (!frame.endsDrag()) openItem()
			}}
			onKeyDown={(e) => {
				if (e.key === 'Enter' || e.key === ' ') openItem()
			}}
			onContextMenu={itemMenu(node.id)}
		>
			<span className="rail-row-icon">
				<ItemIcon link={node} tab={tab} />
			</span>
			<span className="rail-row-title">{node.title}</span>
		</div>
	)
}
