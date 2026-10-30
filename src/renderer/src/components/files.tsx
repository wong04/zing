import type { ReactNode } from 'react'
import { extension, fileName, folderName } from './filePath'
import { CopyIcon, FolderOpenIcon } from './icons'
import { FadingScroll } from './ScrollFade'

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
}

/** A file's icon with its name and an optional second line (Krackn `FileRow`). */
export function FileRow({ path, subtitle, iconSize = 20, selected = false }: FileRowProps) {
	return (
		<span className={selected ? 'file-row selected' : 'file-row'}>
			<FileIcon path={path} size={iconSize} />
			<span className="file-row-text">
				<span className="file-row-name">{fileName(path)}</span>
				{subtitle ? <span className="file-row-subtitle">{subtitle}</span> : null}
			</span>
		</span>
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
