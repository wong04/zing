import type { Session, WebContents } from 'electron'
import type { PermissionKind } from '@shared/browser/pageUi'
import type { PageUiHost } from './pageui-layer'

/** Needed for ordinary page features and never worth a prompt. */
const ALWAYS_ALLOWED = new Set(['fullscreen', 'clipboard-sanitized-write', 'pointerLock'])

interface Pending {
	readonly id: number
	readonly contents: WebContents
	readonly origin: string
	readonly kind: PermissionKind
	readonly grants: readonly string[]
	readonly callback: (granted: boolean) => void
}

export interface PermissionDetails {
	readonly requestingUrl?: string
	readonly mediaTypes?: readonly string[]
}

function originOf(url: string | undefined): string {
	return url && URL.canParse(url) ? new URL(url).origin : 'this page'
}

function mediaKind(types: readonly string[]): PermissionKind | undefined {
	const video = types.includes('video')
	const audio = types.includes('audio')
	if (video && audio) return 'camera and microphone'
	if (video) return 'camera'
	return audio ? 'microphone' : undefined
}

/** The keys a request would be remembered under for the tab's session. */
function grantKeys(origin: string, permission: string, types: readonly string[]): string[] {
	if (permission === 'media') return types.map((t) => `${origin}|media:${t}`)
	return [`${origin}|${permission}`]
}

function kindOf(permission: string, types: readonly string[]): PermissionKind | undefined {
	if (permission === 'media') return mediaKind(types)
	if (permission === 'geolocation') return 'location'
	return permission === 'notifications' ? 'notifications' : undefined
}

/**
 * Camera, microphone, location and notifications are asked for in the panels
 * view; everything else not needed by ordinary pages is refused. A grant lasts
 * only as long as the tab's WebContents, never longer, matching the
 * keeps-nothing model.
 */
export class PermissionBroker {
	readonly #granted = new WeakMap<WebContents, Set<string>>()
	#queue: Pending[] = []
	#nextId = 1

	constructor(private readonly panels: PageUiHost) {}

	/** Every page WebContents is attached once. */
	attach(contents: WebContents): void {
		contents.on('did-start-navigation', (details) => {
			if (!details.isMainFrame || details.isSameDocument) return
			this.#dropFor(contents)
		})
		contents.once('destroyed', () => this.#dropFor(contents))
	}

	request(contents: WebContents, permission: string, details: PermissionDetails, callback: (granted: boolean) => void): void {
		if (ALWAYS_ALLOWED.has(permission)) {
			callback(true)
			return
		}
		const origin = originOf(details.requestingUrl ?? contents.getURL())
		const types = details.mediaTypes ?? []
		const kind = kindOf(permission, types)
		if (kind === undefined) {
			callback(false)
			return
		}
		const grants = grantKeys(origin, permission, types)
		const held = this.#granted.get(contents)
		if (held && grants.every((g) => held.has(g))) {
			callback(true)
			return
		}
		this.#queue.push({ id: this.#nextId++, contents, origin, kind, grants, callback })
		this.#showNext()
	}

	check(contents: WebContents | null, permission: string, origin: string, mediaType: string | undefined): boolean {
		if (ALWAYS_ALLOWED.has(permission)) return true
		if (!contents) return false
		const key = permission === 'media' ? `${origin}|media:${mediaType}` : `${origin}|${permission}`
		return this.#granted.get(contents)?.has(key) ?? false
	}

	answer(id: number, allow: boolean): void {
		const at = this.#queue.findIndex((p) => p.id === id)
		if (at === -1) return
		const [pending] = this.#queue.splice(at, 1)
		if (!pending) return
		if (allow) this.#remember(pending)
		pending.callback(allow)
		this.#showNext()
	}

	#remember(pending: Pending): void {
		const held = this.#granted.get(pending.contents) ?? new Set<string>()
		for (const key of pending.grants) held.add(key)
		this.#granted.set(pending.contents, held)
	}

	#dropFor(contents: WebContents): void {
		const dropped = this.#queue.filter((p) => p.contents === contents)
		if (dropped.length === 0) {
			this.#granted.delete(contents)
			return
		}
		this.#queue = this.#queue.filter((p) => p.contents !== contents)
		for (const pending of dropped) pending.callback(false)
		this.#granted.delete(contents)
		this.#showNext()
	}

	#showNext(): void {
		const next = this.#queue[0]
		this.panels.set({ permission: next && { id: next.id, origin: next.origin, kind: next.kind } })
	}
}

/**
 * The handlers are per session, so one registration routes to whichever
 * window owns the requesting page. A page no window owns is always refused.
 */
export function installPermissions(session: Session, brokerFor: (contents: WebContents | null) => PermissionBroker | undefined): void {
	session.setPermissionRequestHandler((contents, permission, callback, details) => {
		const broker = brokerFor(contents)
		if (!broker) {
			callback(false)
			return
		}
		const media = (details as { mediaTypes?: string[] }).mediaTypes
		broker.request(contents, permission, { requestingUrl: details.requestingUrl, ...(media && { mediaTypes: media }) }, callback)
	})
	session.setPermissionCheckHandler((contents, permission, requestingOrigin, details) => {
		const broker = brokerFor(contents)
		if (ALWAYS_ALLOWED.has(permission)) return true
		if (!broker) return false
		return broker.check(contents, permission, requestingOrigin, (details as { mediaType?: string }).mediaType)
	})
}
