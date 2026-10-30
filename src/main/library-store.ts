import { readFileSync } from 'node:fs'
import {
	emptyLibrary,
	FAVOURITES_ID,
	favourites,
	find,
	folder,
	groupIntoFolder,
	isFolder,
	link,
	move,
	readingItem,
	remove,
	rename,
	urls,
	withFavourites,
	type BookmarkNode,
	type BrowserLibrary,
	type ReadingItem,
} from '@shared/library/library'
import type { LibraryAction } from '@shared/ipc'
import { DebouncedJsonWriter } from './settings-file'

export class LibraryFileError extends Error {
	constructor(
		readonly path: string,
		detail: string,
		options?: { cause: unknown },
	) {
		super(`Could not read the library from ${path}: ${detail}`, options)
		this.name = 'LibraryFileError'
	}
}

export class InvalidLibraryActionError extends Error {
	constructor(detail: string) {
		super(`invalid library action: ${detail}`)
		this.name = 'InvalidLibraryActionError'
	}
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
	typeof value === 'object' && value !== null && !Array.isArray(value)

function parseNode(raw: unknown, where: string): BookmarkNode {
	if (!isRecord(raw)) throw new TypeError(`${where} is not an object`)
	const { id, title, updatedAt } = raw
	if (typeof id !== 'string' || typeof title !== 'string' || typeof updatedAt !== 'number') {
		throw new TypeError(`${where} needs a string id and title and a numeric updatedAt`)
	}
	if ('children' in raw) {
		if (!Array.isArray(raw['children'])) throw new TypeError(`${where}.children is not a list`)
		return { id, title, updatedAt, children: raw['children'].map((c, i) => parseNode(c, `${where}.children[${i}]`)) }
	}
	if (typeof raw['url'] !== 'string') throw new TypeError(`${where} has no url`)
	return { id, title, updatedAt, url: raw['url'] }
}

function parseReading(raw: unknown, where: string): ReadingItem {
	if (!isRecord(raw)) throw new TypeError(`${where} is not an object`)
	const { id, title, url, added, isRead, updatedAt } = raw
	if (
		typeof id !== 'string' ||
		typeof title !== 'string' ||
		typeof url !== 'string' ||
		typeof added !== 'number' ||
		typeof isRead !== 'boolean' ||
		typeof updatedAt !== 'number'
	) {
		throw new TypeError(`${where} is not a reading list item`)
	}
	return { id, title, url, added, isRead, updatedAt }
}

/** Throws a TypeError naming the first malformed field; the Favourites folder is restored if the file lacks it. */
export function parseLibrary(raw: unknown): BrowserLibrary {
	if (!isRecord(raw) || !Array.isArray(raw['bookmarks']) || !Array.isArray(raw['readingList'])) {
		throw new TypeError('expected an object with bookmarks and readingList lists')
	}
	const bookmarks = raw['bookmarks'].map((n, i) => parseNode(n, `bookmarks[${i}]`))
	const readingList = raw['readingList'].map((n, i) => parseReading(n, `readingList[${i}]`))
	const library: BrowserLibrary = { bookmarks, readingList }
	return bookmarks.some((n) => n.id === FAVOURITES_ID) ? library : withFavourites(library, [])
}

/** No file means first run. Anything unreadable is an error, never a silent empty library that would overwrite it. */
export function loadLibraryFile(path: string): BrowserLibrary {
	let raw: string
	try {
		raw = readFileSync(path, 'utf8')
	} catch (error) {
		if ((error as NodeJS.ErrnoException).code === 'ENOENT') return emptyLibrary()
		throw new LibraryFileError(path, (error as Error).message, { cause: error })
	}
	try {
		return parseLibrary(JSON.parse(raw))
	} catch (error) {
		throw new LibraryFileError(path, (error as Error).message, { cause: error })
	}
}

/** `to` is an insertion point in the node's own sibling list (the `move` convention). Undefined when `id` is not in `nodes`. */
export function moveNode(nodes: BookmarkNode[], id: string, to: number): BookmarkNode[] | undefined {
	const from = nodes.findIndex((n) => n.id === id)
	if (from >= 0) return move(nodes, from, to)
	for (const [index, node] of nodes.entries()) {
		if (!isFolder(node)) continue
		const children = moveNode(node.children, id, to)
		if (!children) continue
		const copy = [...nodes]
		copy[index] = { ...node, children }
		return copy
	}
	return undefined
}

const ACTION_TYPES = ['rename', 'remove', 'newFolder', 'move', 'setRead'] as const

const strings = (value: unknown, name: string): string[] => {
	if (!Array.isArray(value) || !value.every((v) => typeof v === 'string')) {
		throw new InvalidLibraryActionError(`${name} must be a list of strings`)
	}
	return value as string[]
}

export function parseLibraryAction(raw: unknown): LibraryAction {
	if (!isRecord(raw)) throw new InvalidLibraryActionError('not an object')
	const type = raw['type']
	if (!(ACTION_TYPES as readonly unknown[]).includes(type)) throw new InvalidLibraryActionError(`unknown type ${String(type)}`)
	switch (type as (typeof ACTION_TYPES)[number]) {
		case 'rename':
			if (typeof raw['id'] !== 'string' || typeof raw['title'] !== 'string') throw new InvalidLibraryActionError('rename needs id and title')
			return { type: 'rename', id: raw['id'], title: raw['title'] }
		case 'remove':
			return { type: 'remove', ids: strings(raw['ids'], 'ids') }
		case 'newFolder':
			return { type: 'newFolder', ids: strings(raw['ids'], 'ids') }
		case 'move':
			if (typeof raw['id'] !== 'string' || !Number.isInteger(raw['to'])) throw new InvalidLibraryActionError('move needs id and an integer to')
			return { type: 'move', id: raw['id'], to: raw['to'] as number }
		case 'setRead':
			if (typeof raw['id'] !== 'string' || typeof raw['read'] !== 'boolean') throw new InvalidLibraryActionError('setRead needs id and read')
			return { type: 'setRead', id: raw['id'], read: raw['read'] }
	}
}

export type AddOutcome = 'added' | 'exists'

type Listener = (library: BrowserLibrary) => void

/**
 * Bookmarks, the Favourites bar and the Reading List, shared by every window.
 * The one store the browser writes on purpose: each entry is something the
 * user chose to keep, and browsing itself adds nothing.
 */
export class LibraryStore {
	#library: BrowserLibrary
	readonly #listeners = new Set<Listener>()

	constructor(
		initial: BrowserLibrary,
		private readonly writer: Pick<DebouncedJsonWriter, 'schedule' | 'flush'>,
	) {
		this.#library = initial
	}

	snapshot(): BrowserLibrary {
		return this.#library
	}

	subscribe(listener: Listener): () => void {
		this.#listeners.add(listener)
		return () => this.#listeners.delete(listener)
	}

	flush(): void {
		this.writer.flush()
	}

	addFavourite(title: string, url: string): AddOutcome {
		const bar = favourites(this.#library)
		if (bar.some((n) => !isFolder(n) && n.url === url)) return 'exists'
		this.#set(withFavourites(this.#library, [...bar, link(title, url)]))
		return 'added'
	}

	addReading(title: string, url: string): AddOutcome {
		if (this.#library.readingList.some((i) => i.url === url)) return 'exists'
		this.#set({ ...this.#library, readingList: [readingItem(title, url), ...this.#library.readingList] })
		return 'added'
	}

	/** The id of a folder this action created, so the panel can start renaming it. */
	apply(action: LibraryAction): string | undefined {
		switch (action.type) {
			case 'rename':
				this.#set({ ...this.#library, bookmarks: rename(action.id, action.title, this.#library.bookmarks) })
				return undefined
			case 'remove':
				return this.#remove(new Set(action.ids))
			case 'newFolder':
				return this.#newFolder(action.ids)
			case 'move':
				return this.#move(action.id, action.to)
			case 'setRead':
				this.#set({
					...this.#library,
					readingList: this.#library.readingList.map((i) => (i.id === action.id ? { ...i, isRead: action.read, updatedAt: Date.now() } : i)),
				})
				return undefined
		}
	}

	/** Where a bookmark or reading list item points. */
	urlOf(id: string): string | undefined {
		const node = find(id, this.#library.bookmarks)
		if (node) return isFolder(node) ? undefined : node.url
		return this.#library.readingList.find((i) => i.id === id)?.url
	}

	childrenOf(id: string): BookmarkNode[] {
		const node = find(id, this.#library.bookmarks)
		return node && isFolder(node) ? node.children : []
	}

	isFolderNode(id: string): boolean {
		const node = find(id, this.#library.bookmarks)
		return node !== undefined && isFolder(node)
	}

	/** Every link under a node, depth-first: what Ctrl-clicking a bar folder opens. */
	urlsOf(id: string): string[] {
		const node = find(id, this.#library.bookmarks)
		return node ? urls(node) : []
	}

	#remove(ids: ReadonlySet<string>): undefined {
		this.#set({
			bookmarks: remove(ids, this.#library.bookmarks),
			readingList: this.#library.readingList.filter((i) => !ids.has(i.id)),
		})
		return undefined
	}

	#newFolder(ids: readonly string[]): string {
		if (ids.length > 0) {
			const grouped = groupIntoFolder(new Set(ids), 'New Folder', this.#library.bookmarks)
			if (grouped.folderId !== undefined) {
				this.#set({ ...this.#library, bookmarks: grouped.nodes })
				return grouped.folderId
			}
		}
		const created = folder('New Folder')
		this.#set({ ...this.#library, bookmarks: [...this.#library.bookmarks, created] })
		return created.id
	}

	#move(id: string, to: number): undefined {
		const bookmarks = moveNode(this.#library.bookmarks, id, to)
		if (bookmarks) {
			this.#set({ ...this.#library, bookmarks })
			return undefined
		}
		const from = this.#library.readingList.findIndex((i) => i.id === id)
		if (from < 0) return undefined
		this.#set({ ...this.#library, readingList: move(this.#library.readingList, from, to) })
		return undefined
	}

	#set(next: BrowserLibrary): void {
		if (next === this.#library) return
		this.#library = next
		this.writer.schedule(next)
		for (const listener of this.#listeners) listener(next)
	}
}

/** `ZING_LIBRARY_FILE` lets harnesses use a scratch file so they never touch real bookmarks. */
export function openLibrary(defaultPath: string): LibraryStore {
	const path = process.env['ZING_LIBRARY_FILE'] || defaultPath
	return new LibraryStore(loadLibraryFile(path), new DebouncedJsonWriter(path))
}
