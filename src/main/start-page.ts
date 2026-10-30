import { protocol, type Session, type WebContents } from 'electron'
import {
	editShortcutsHost,
	restyleScript,
	startPageHTML,
	startPageScheme,
	type StartPageTheme,
	type StartPageTokens,
} from '@shared/browser/startPage'
import { decode, encode } from '@shared/browser/startShortcuts'
import { accentInk, control, raised, type PaletteTokens } from '@shared/theme/palette'
import { darkTokens, lightTokens } from '@shared/theme/accent'
import type { Settings, SettingsStore } from '@shared/settings'
import { searchFormAction } from './address'

export class StartPageSchemeError extends Error {
	constructor(readonly url: string) {
		super(`${url} is not a page the zing: scheme serves`)
		this.name = 'StartPageSchemeError'
	}
}

/** Must run before the app is ready; `standard` is what lets `zing://home` parse with a host. */
export function registerStartPageScheme(): void {
	protocol.registerSchemesAsPrivileged([{ scheme: startPageScheme, privileges: { standard: true, secure: true } }])
}

function startPageTokens(p: PaletteTokens): StartPageTokens {
	return {
		background: p.background,
		surface: p.surface,
		tint: p.tint,
		well: p.well,
		outline: p.outline,
		accent: p.accent,
		onAccent: p.onAccent,
		text: p.text,
		textSecondary: p.textSecondary,
		control: control(p),
		raised: raised(p),
		accentInk: accentInk(p),
	}
}

export function startPageTheme(accent: Settings['accent']): StartPageTheme {
	return { light: startPageTokens(lightTokens(accent)), dark: startPageTokens(darkTokens(accent)) }
}

export function isStartPage(url: string): boolean {
	if (!URL.canParse(url)) return false
	const parsed = new URL(url)
	return parsed.protocol === `${startPageScheme}:` && parsed.hostname === 'home'
}

export type StartPageNavigation =
	| { readonly kind: 'search'; readonly query: string }
	| { readonly kind: 'edit-shortcuts' }
	/** A link to the edit-shortcuts address from anywhere but the start page: never loaded, never acted on. */
	| { readonly kind: 'block' }
	| { readonly kind: 'allow' }

/**
 * What a navigation starting on `fromUrl` should do. Only the start page's
 * own search form and Edit shortcuts link are special, so no website can
 * open Settings by linking to the edit-shortcuts address.
 */
export function classifyNavigation(fromUrl: string, toUrl: string, engine: Settings['searchEngine']): StartPageNavigation {
	if (!URL.canParse(toUrl)) return { kind: 'allow' }
	const to = new URL(toUrl)
	const fromStartPage = isStartPage(fromUrl)
	if (to.protocol === `${startPageScheme}:` && to.hostname === editShortcutsHost) {
		return fromStartPage ? { kind: 'edit-shortcuts' } : { kind: 'block' }
	}
	if (!fromStartPage) return { kind: 'allow' }
	const action = new URL(searchFormAction(engine))
	const query = to.searchParams.get('q')
	if (query === null || to.origin !== action.origin || to.pathname !== action.pathname) return { kind: 'allow' }
	return { kind: 'search', query }
}

export interface StartPageHandlers {
	/** Runs the typed text through the address bar's own rule. */
	readonly onSearch: (contents: WebContents, query: string) => void
	readonly onEditShortcuts: () => void
}

/** The new-tab page, served from the app so opening a tab never touches the network. */
export class StartPage {
	constructor(private readonly settings: SettingsStore) {}

	html(): string {
		const engine = this.settings.get('searchEngine')
		const stored = this.settings.get('startShortcuts')
		return startPageHTML({
			searchURL: searchFormAction(engine),
			shortcuts: decode(stored === null ? null : encode(stored)),
			theme: startPageTheme(this.settings.get('accent')),
		})
	}

	serve(session: Session): void {
		session.protocol.handle(startPageScheme, (request) => {
			if (!isStartPage(request.url)) throw new StartPageSchemeError(request.url)
			return new Response(this.html(), {
				headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' },
			})
		})
	}

	attach(contents: WebContents, handlers: StartPageHandlers): void {
		contents.on('will-navigate', (event, url) => {
			const navigation = classifyNavigation(contents.getURL(), url, this.settings.get('searchEngine'))
			if (navigation.kind === 'allow') return
			event.preventDefault()
			if (navigation.kind === 'search') handlers.onSearch(contents, navigation.query)
			if (navigation.kind === 'edit-shortcuts') handlers.onEditShortcuts()
		})
	}

	/**
	 * Keeps open start pages current: a new accent recolours them in place, a
	 * changed tile list or search engine rebuilds them. Returns the unsubscribe.
	 */
	follow(pages: () => readonly WebContents[]): () => void {
		return this.settings.subscribe((settings, key) => {
			const open = pages().filter((c) => !c.isDestroyed() && isStartPage(c.getURL()))
			if (key === 'accent' || key === 'theme') {
				const script = restyleScript(startPageTheme(settings.accent))
				for (const contents of open) void contents.executeJavaScript(script)
				return
			}
			if (key === 'startShortcuts' || key === 'searchEngine') for (const contents of open) contents.reload()
		})
	}
}
