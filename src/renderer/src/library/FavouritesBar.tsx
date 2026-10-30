import { useState, type DragEvent, type MouseEvent } from 'react'
import { favourites, isFolder, type BookmarkNode, type BrowserLibrary } from '@shared/library/library'
import { zing } from '../api'

const MIDDLE_BUTTON = 1

function Chip({ node, onDragOver, onDrop, dropSide }: {
	node: BookmarkNode
	onDragOver: (e: DragEvent<HTMLElement>) => void
	onDrop: (e: DragEvent<HTMLElement>) => void
	dropSide: 'before' | 'after' | undefined
}) {
	const folder = isFolder(node)
	const onClick = (e: MouseEvent<HTMLButtonElement>): void => {
		if (!folder) {
			void zing.openLibraryItem(node.id, e.ctrlKey ? 'tab' : 'current')
			return
		}
		if (e.ctrlKey) {
			void zing.openAllInFolder(node.id)
			return
		}
		const rect = e.currentTarget.getBoundingClientRect()
		void zing.openFolderMenu(node.id, { x: rect.left, y: rect.bottom })
	}
	const onAuxClick = (e: MouseEvent<HTMLButtonElement>): void => {
		if (e.button !== MIDDLE_BUTTON) return
		if (folder) void zing.openAllInFolder(node.id)
		else void zing.openLibraryItem(node.id, 'tab')
	}
	const onContextMenu = (e: MouseEvent<HTMLButtonElement>): void => {
		e.preventDefault()
		void zing.openLibraryItemMenu(node.id, { x: e.clientX, y: e.clientY })
	}
	return (
		<button
			type="button"
			className={['fav-chip', folder && 'folder', dropSide && `drop-${dropSide}`].filter(Boolean).join(' ')}
			title={folder ? `${node.title} (Ctrl-click opens all)` : node.title}
			draggable
			onDragStart={(e) => e.dataTransfer.setData('text/x-zing-fav', node.id)}
			onDragOver={onDragOver}
			onDrop={onDrop}
			onClick={onClick}
			onAuxClick={onAuxClick}
			onContextMenu={onContextMenu}
		>
			<span className="fav-glyph" aria-hidden="true">{folder ? '▸' : node.title.slice(0, 1).toUpperCase()}</span>
			<span className="fav-title">{node.title}</span>
		</button>
	)
}

export function FavouritesBar({ library }: { library: BrowserLibrary }) {
	const items = favourites(library)
	const [over, setOver] = useState<{ id: string; side: 'before' | 'after' } | undefined>()

	const sideOf = (e: DragEvent<HTMLElement>): 'before' | 'after' => {
		const rect = e.currentTarget.getBoundingClientRect()
		return e.clientX < rect.left + rect.width / 2 ? 'before' : 'after'
	}
	const dropOn = (target: BookmarkNode) => (e: DragEvent<HTMLElement>): void => {
		e.preventDefault()
		setOver(undefined)
		const id = e.dataTransfer.getData('text/x-zing-fav')
		const index = items.findIndex((n) => n.id === target.id)
		if (!id || index < 0) return
		void zing.applyLibrary({ type: 'move', id, to: sideOf(e) === 'before' ? index : index + 1 })
	}

	return (
		<div className="favourites-bar" role="toolbar" aria-label="Favourites" onDragLeave={() => setOver(undefined)}>
			{items.length === 0 ? <span className="fav-hint">Press Ctrl+D to add this page to Favourites</span> : null}
			{items.map((node) => (
				<Chip
					key={node.id}
					node={node}
					dropSide={over?.id === node.id ? over.side : undefined}
					onDragOver={(e) => {
						e.preventDefault()
						setOver({ id: node.id, side: sideOf(e) })
					}}
					onDrop={dropOn(node)}
				/>
			))}
		</div>
	)
}
