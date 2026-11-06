import { useEffect, useState, type DragEvent, type ReactNode } from 'react'
import type { PickedFile } from '../../../shared/engines/engineJob'
import { zing } from '../api'
import { extension, fileName, folderName } from './filePath'
import { CopyIcon, FolderOpenIcon } from './icons'
import { PillButton } from './PillButton'
import { FadingScroll } from './ScrollFade'
import { beginToolDrag, droppedToolFile, endToolDrag, isToolDrag } from './toolDrag'

export interface FileIconProps {
	readonly path: string
	readonly size?: number
}

/**
 * A file's icon: a tile carrying its extension. The system's own file icons
 * come from the main process and are a later addition to this component.
 */
export function FileIcon({ path, size = 18 }: FileIconProps) {
	const ext = extension(path)
	return (
		<span className="file-icon" style={{ width: size, height: size, fontSize: Math.max(7, size * 0.38) }} aria-hidden="true">
			{ext.slice(0, 4) || '·'}
		</span>
	)
}

export interface FileRowProps {
	readonly path: string
	readonly subtitle?: string
	readonly iconSize?: number
	readonly selected?: boolean
	/** A file this tool made: the row can be dragged into another tool's drop zone, which gets the file by its token. */
	readonly dragFile?: PickedFile
}

/** A file's icon with its name and an optional second line (Krackn `FileRow`). */
export function FileRow({ path, subtitle, iconSize = 20, selected = false, dragFile }: FileRowProps) {
	const classes = ['file-row', selected ? 'selected' : '', dragFile ? 'draggable' : ''].filter(Boolean).join(' ')
	return (
		<span
			className={classes}
			draggable={dragFile ? true : undefined}
			title={dragFile ? 'Drag this into another tool' : undefined}
			onDragStart={dragFile ? (event) => beginToolDrag(event.dataTransfer, dragFile) : undefined}
			onDragEnd={dragFile ? endToolDrag : undefined}
		>
			<FileIcon path={path} size={iconSize} />
			<span className="file-row-text">
				<span className="file-row-name">{fileName(path)}</span>
				{subtitle ? <span className="file-row-subtitle">{subtitle}</span> : null}
			</span>
		</span>
	)
}

export interface CopyFileButtonProps {
	readonly file: PickedFile
	/** The host refused or the clipboard could not be written. */
	readonly onError: (error: Error) => void
}

const COPIED_SHOWN_MS = 2000

/** Puts a file this tool made on the clipboard as a file, so it pastes into Explorer, a mail or a chat. */
export function CopyFileButton({ file, onError }: CopyFileButtonProps) {
	const [copied, setCopied] = useState(false)
	useEffect(() => {
		if (!copied) return
		const timer = setTimeout(() => setCopied(false), COPIED_SHOWN_MS)
		return () => clearTimeout(timer)
	}, [copied])
	return (
		<PillButton
			size="small"
			variant="outlined"
			icon={<CopyIcon size={13} />}
			aria-label={`Copy ${file.name} to the clipboard`}
			onClick={() => zing.copyFileToClipboard(file.token).then(() => setCopied(true), onError)}
		>
			{copied ? 'Copied' : 'Copy file'}
		</PillButton>
	)
}

export interface RecentFilesListProps {
	readonly files: readonly string[]
	readonly maxHeight?: number
	readonly onSelect: (path: string) => void
	readonly onReveal?: (path: string) => void
	readonly onCopyPath?: (path: string) => void
}

/** The shared "Recent" list under every file tool's empty state (Krackn `RecentFilesList`). */
export function RecentFilesList({ files, maxHeight = 190, onSelect, onReveal, onCopyPath }: RecentFilesListProps) {
	return (
		<div className="recent-files">
			<div className="recent-files-heading">
				<span>RECENT</span>
				<span className="recent-files-count">{files.length}</span>
			</div>
			<FadingScroll fadeHeight={22} maxHeight={maxHeight} aria-label="Recent files">
				<ul className="recent-files-list">
					{files.map((path) => (
						<li key={path} className="recent-row">
							<button type="button" className="recent-row-main" title={path} onClick={() => onSelect(path)}>
								<FileRow path={path} subtitle={folderName(path)} iconSize={19} />
							</button>
							{onReveal ? (
								<button
									type="button"
									className="recent-row-action"
									aria-label={`Show ${fileName(path)} in Explorer`}
									title="Show in Explorer"
									onClick={() => onReveal(path)}
								>
									<FolderOpenIcon size={14} />
								</button>
							) : null}
							{onCopyPath ? (
								<button
									type="button"
									className="recent-row-action"
									aria-label={`Copy path of ${fileName(path)}`}
									title="Copy path"
									onClick={() => onCopyPath(path)}
								>
									<CopyIcon size={14} />
								</button>
							) : null}
						</li>
					))}
				</ul>
			</FadingScroll>
		</div>
	)
}

export interface ToolEmptyStateProps {
	readonly icon: ReactNode
	readonly title: string
	readonly message: string
	/** A file is being dragged over the tool. */
	readonly targeted?: boolean
	readonly recents?: readonly string[]
	readonly onSelectRecent?: (path: string) => void
	readonly onRevealRecent?: (path: string) => void
	readonly children?: ReactNode
}

/** What a file tool shows before anything is open (Krackn `ToolEmptyState`). */
export function ToolEmptyState({
	icon,
	title,
	message,
	targeted = false,
	recents = [],
	onSelectRecent,
	onRevealRecent,
	children,
}: ToolEmptyStateProps) {
	return (
		<div className={targeted ? 'tool-empty targeted' : 'tool-empty'}>
			<div className="tool-empty-inner">
				<div className="tool-empty-icon">{icon}</div>
				<div className="tool-empty-text">
					<h2>{title}</h2>
					<p>{message}</p>
				</div>
				{children}
				{recents.length > 0 && onSelectRecent ? (
					<div className="tool-empty-recents">
						<RecentFilesList files={recents} onSelect={onSelectRecent} onReveal={onRevealRecent} />
					</div>
				) : null}
			</div>
		</div>
	)
}

export interface FileDropZoneProps {
	/** What the drop gave: a name for each file and the token to hand an engine job. The path stays in the host. */
	readonly onFiles: (files: readonly PickedFile[]) => void
	/** The host refused the drop or couldn't read it. */
	readonly onError: (error: Error) => void
	readonly children: (state: { readonly targeted: boolean }) => ReactNode
}

const isFileDrag = (event: DragEvent): boolean => event.dataTransfer.types.includes('Files')
const isAcceptedDrag = (event: DragEvent): boolean => isFileDrag(event) || isToolDrag(event.dataTransfer.types)

class ForeignToolDragError extends Error {
	override name = 'ForeignToolDragError'
	constructor() {
		super('That drag is not a file from one of the tools, so it was not taken.')
	}
}

/**
 * Takes files dropped on a Zing view (Spike S4). The host reads each file's path from the drop itself, so what comes
 * back is a token and a name; a page can't do this, since only Zing's own views reach the host. A file another tool
 * made (a `FileRow` with `dragFile`) is taken by its token and never touches the host. Text and links are refused.
 */
export function FileDropZone({ onFiles, onError, children }: FileDropZoneProps) {
	const [targeted, setTargeted] = useState(false)
	return (
		<div
			onDragEnter={(event) => {
				if (!isAcceptedDrag(event)) return
				event.preventDefault()
				setTargeted(true)
			}}
			onDragOver={(event) => {
				if (isAcceptedDrag(event)) event.preventDefault()
			}}
			onDragLeave={(event) => {
				if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setTargeted(false)
			}}
			onDrop={(event) => {
				if (!isAcceptedDrag(event)) return
				event.preventDefault()
				setTargeted(false)
				if (isToolDrag(event.dataTransfer.types)) {
					const file = droppedToolFile(event.dataTransfer)
					if (file === undefined) onError(new ForeignToolDragError())
					else onFiles([file])
					return
				}
				zing.openDropped([...event.dataTransfer.files]).then(onFiles, onError)
			}}
		>
			{children({ targeted })}
		</div>
	)
}
