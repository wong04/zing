import { registrableDomain } from '@shared/browser/registrableDomain'

export interface SiteToPurge {
	readonly domain: string
	readonly origins: readonly string[]
}

/**
 * Which sites each tab has touched. A site's data is purged when the last
 * tab that touched it goes, keyed by registrable domain so `mail.x.com` and
 * `docs.x.com` are one site, as everywhere else in the app.
 */
export class SiteUsage {
	readonly #domainsByTab = new Map<string, Set<string>>()
	readonly #originsByDomain = new Map<string, Set<string>>()

	visit(tabKey: string, url: string): void {
		if (!URL.canParse(url)) return
		const { protocol, hostname, origin } = new URL(url)
		if (protocol !== 'http:' && protocol !== 'https:') return
		const domain = registrableDomain(hostname)
		let domains = this.#domainsByTab.get(tabKey)
		if (!domains) this.#domainsByTab.set(tabKey, (domains = new Set()))
		domains.add(domain)
		let origins = this.#originsByDomain.get(domain)
		if (!origins) this.#originsByDomain.set(domain, (origins = new Set()))
		origins.add(origin)
	}

	/** Forgets the tab and returns the sites that no remaining tab is using. */
	close(tabKey: string): SiteToPurge[] {
		const domains = this.#domainsByTab.get(tabKey)
		this.#domainsByTab.delete(tabKey)
		if (!domains) return []
		const stillOpen = new Set([...this.#domainsByTab.values()].flatMap((d) => [...d]))
		const gone: SiteToPurge[] = []
		for (const domain of domains) {
			if (stillOpen.has(domain)) continue
			gone.push({ domain, origins: [...(this.#originsByDomain.get(domain) ?? [])] })
			this.#originsByDomain.delete(domain)
		}
		return gone
	}
}
