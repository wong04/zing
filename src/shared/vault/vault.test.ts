import { describe, expect, it } from 'vitest'
import { parsePasswordCsv } from '../import/passwords'
import {
	canFill,
	emptyVault,
	entriesFor,
	importLogins,
	NotAWebOriginError,
	originOf,
	parseVault,
	removeEntry,
	saveLogin,
	serializeVault,
	vaultKeepsData,
	VaultFormatError,
} from './vault'

describe('origins', () => {
	it('normalises case and default ports', () => expect(originOf('HTTPS://Accounts.Example.com:443/login?x=1')).toBe('https://accounts.example.com'))
	it('keeps non-default ports distinct', () => expect(originOf('https://a.example:8443/')).toBe('https://a.example:8443'))
	it('http and https are different origins', () => expect(originOf('http://a.example')).not.toBe(originOf('https://a.example')))
	it('refuses non-web schemes', () => expect(() => originOf('file:///C:/x.html')).toThrow(NotAWebOriginError))
})

describe('who gets offered a password', () => {
	let vault = emptyVault()
	vault = saveLogin(vault, { url: 'https://accounts.example.com/login', username: 'me', password: 'pw' }, 1).vault
	vault = saveLogin(vault, { url: 'https://accounts.example.com/', username: 'alt', password: 'pw2' }, 2).vault

	it('the exact origin, newest first', () => {
		const hits = entriesFor(vault, { topUrl: 'https://accounts.example.com/signin', frameUrl: 'https://accounts.example.com/signin' })
		expect(hits.map((e) => e.username)).toEqual(['alt', 'me'])
	})
	it('not a sibling subdomain', () => {
		expect(entriesFor(vault, { topUrl: 'https://evil.example.com/', frameUrl: 'https://evil.example.com/' })).toEqual([])
	})
	it('not the registrable domain', () => {
		expect(entriesFor(vault, { topUrl: 'https://example.com/', frameUrl: 'https://example.com/' })).toEqual([])
	})
	it('a same-origin frame can be filled', () => {
		expect(canFill({ topUrl: 'https://accounts.example.com/a', frameUrl: 'https://accounts.example.com/frame' })).toBe(true)
	})
	it('a cross-origin frame is never filled, even with its own saved logins', () => {
		const context = { topUrl: 'https://shop.example/', frameUrl: 'https://accounts.example.com/embed' }
		expect(canFill(context)).toBe(false)
		expect(entriesFor(vault, context)).toEqual([])
	})
})

describe('saving', () => {
	const first = saveLogin(emptyVault(), { url: 'https://a.example/login', username: 'me', password: 'old' }, 1)
	it('adds a new login, titled by host', () => {
		expect(first.outcome.kind).toBe('added')
		expect(first.outcome.entry.title).toBe('a.example')
	})
	it('a changed password updates the same entry', () => {
		const second = saveLogin(first.vault, { url: 'https://a.example/other', username: 'me', password: 'new' }, 5)
		expect(second.outcome.kind).toBe('updated')
		expect(second.vault.entries).toHaveLength(1)
		expect(second.vault.entries[0]?.password).toBe('new')
		expect(second.vault.entries[0]?.updatedAt).toBe(5)
	})
	it('saving the same thing again changes nothing', () => {
		const again = saveLogin(first.vault, { url: 'https://a.example/login', username: 'me', password: 'old' }, 9)
		expect(again.outcome.kind).toBe('unchanged')
		expect(again.vault).toBe(first.vault)
	})
	it('removing an unknown entry is an error, not a no-op', () => expect(() => removeEntry(first.vault, 'nope')).toThrow(VaultFormatError))
	it('removes by id', () => expect(removeEntry(first.vault, first.outcome.entry.id).entries).toEqual([]))
})

describe('importing a CSV export into the vault', () => {
	const csv = 'name,url,username,password,note\nA,https://a.example/,u1,p1,\nA again,https://a.example/login,u1,p1,\nB,https://b.example/,u2,p2,\n'
	const { vault, summary } = importLogins(emptyVault(), parsePasswordCsv(csv).logins, 1)
	it('dedupes by origin and username', () => {
		expect(summary).toEqual({ added: 2, updated: 0, unchanged: 1 })
		expect(vault.entries).toHaveLength(2)
	})
	it('flips the privacy banner once it holds anything', () => {
		expect(vaultKeepsData(emptyVault())).toBe(false)
		expect(vaultKeepsData(vault)).toBe(true)
	})
})

describe('on-disk shape', () => {
	const { vault } = saveLogin(emptyVault(), { url: 'https://a.example/', username: 'u', password: 'p' }, 1)
	it('round-trips', () => expect(parseVault(serializeVault(vault))).toEqual(vault))
	it('rejects an unknown version', () => expect(() => parseVault('{"version":2,"entries":[]}')).toThrow(VaultFormatError))
	it('rejects a malformed entry', () => {
		expect(() => parseVault('{"version":1,"entries":[{"id":"x"}]}')).toThrow(/Entry 0: "origin" must be a string/)
	})
})
