import { describe, expect, it } from 'vitest'
import { declinedNotice, dohNotice } from './dohNotice'

describe('dohNotice', () => {
	it('names the provider and says it receives the site name, and that own-network names are kept back', () => {
		const text = dohNotice('Quad9')
		expect(text).toContain('Quad9')
		expect(text).toContain("receives the site's name")
		expect(text).toContain('your own network')
	})
})

describe('declinedNotice', () => {
	it('says what is true: the provider already had the name, the relay and archive got nothing', () => {
		const text = declinedNotice('Cloudflare')
		expect(text).toContain('Cloudflare')
		expect(text).toContain('Nothing was sent to the reader relay or the archive.')
		expect(text).not.toContain('need no outside service')
	})
})
