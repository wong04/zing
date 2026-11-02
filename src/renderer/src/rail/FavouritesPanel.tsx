import { useEffect } from 'react'
import { FAVOURITES_ID, isFolder, type BookmarkNode } from '@shared/library/library'
import { zing } from '../api'
import { FolderIcon } from '../components/icons'
import { ItemIcon, savedFolderProps, savedItemProps, type SavedFrame } from './savedItem'

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
			<div {...place} {...savedFolderProps(frame, node, 'rail-row rail-panel-row', (at) => void zing.openFolderMenu(node.id, at))}>
				<span className="rail-row-icon">
					<FolderIcon size={15} />
				</span>
				<span className="rail-row-title">{node.title}</span>
			</div>
		)
	}
	const openItem = (): void => {
		onClose()
		void zing.openSavedTab(node.id)
	}
	return (
		<div data-item-id={node.id} {...place} {...savedItemProps(frame, node, 'rail-row rail-item rail-panel-row', openItem)}>
			<span className="rail-row-icon">
				<ItemIcon link={node} tab={frame.tabsByItem.get(node.id)} />
			</span>
			<span className="rail-row-title">{node.title}</span>
		</div>
	)
}
