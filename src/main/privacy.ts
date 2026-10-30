import type { Session, WebContents } from 'electron'
import { registrableDomain } from '@shared/browser/registrableDomain'
import { SiteUsage, type SiteToPurge } from './site-usage'

/**
 * Keeps nothing: the session is in memory, and on top of that a site's data
 * is purged the moment the last tab that touched it closes (Krackn's "closing
 * a tab purges that site's data"). No history is recorded anywhere; the only
 * thing remembered is which sites open tabs have used, and that lives in RAM.
 */
export class SiteDataKeeper {
	readonly #session: Session
	readonly #usage = new SiteUsage()
	readonly #live = new Map<string, WebContents>()

	constructor(session: Session) {
		this.#session = session
	}

	/**
	 * Follows what a tab visits. A sleeping tab drops its renderer but keeps its
	 * record, so the key outlives any one WebContents; `release` ends it.
	 */
	track(tabKey: string, contents: WebContents): void {
		this.#live.set(tabKey, contents)
		const visit = (url: string): void => this.#usage.visit(tabKey, url)
		contents.on('did-frame-navigate', (_event, url) => visit(url))
		contents.on('did-navigate-in-page', (_event, url) => visit(url))
	}

	/** The tab is gone for good: purge whatever sites no other open tab is using. */
	release(tabKey: string): void {
		const contents = this.#live.get(tabKey)
		this.#live.delete(tabKey)
		const sites = this.#usage.close(tabKey)
		if (sites.length === 0) return
		const purgeAll = (): void => {
			for (const site of sites) {
				this.purge(site).catch((error: unknown) => console.error(`Could not purge data for ${site.domain}`, error))
			}
		}
		// Purge after the page is gone so nothing can write to the storage again as it is cleared.
		if (contents && !contents.isDestroyed()) contents.once('destroyed', purgeAll)
		else purgeAll()
	}

	async purge(site: SiteToPurge): Promise<void> {
		await Promise.all(site.origins.map((origin) => this.#session.clearStorageData({ origin })))
		await this.#removeDomainCookies(site.domain)
	}

	/** `clearStorageData({origin})` misses cookies another subdomain of the site could set or read. */
	async #removeDomainCookies(domain: string): Promise<void> {
		const cookies = await this.#session.cookies.get({})
		const ours = cookies.filter((c) => registrableDomain((c.domain ?? '').replace(/^\./, '')) === domain)
		await Promise.all(
			ours.map((c) => {
				const host = (c.domain ?? '').replace(/^\./, '')
				return this.#session.cookies.remove(`${c.secure ? 'https' : 'http'}://${host}${c.path ?? '/'}`, c.name)
			}),
		)
	}
}

/**
 * A persistent profile exists so sign-ins survive closing a tab and quitting;
 * purging a site on its last tab would undo exactly that. Purging runs only
 * for the in-memory profile.
 */
export function siteDataKeeperFor(persistentProfile: boolean, session: Session): SiteDataKeeper | undefined {
	if (persistentProfile) return undefined
	return new SiteDataKeeper(session)
}
