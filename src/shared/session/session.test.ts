// Port of Krackn `Tests/unit` section M (SessionStore). The interactionState
// cases are WebKit-only and have no Chromium counterpart; Tests/persist drives
// a live WKWebView and is covered by the e2e suite instead.
import { describe, expect, it } from 'vitest'
import {
	MAX_SESSION_TABS,
	decodeSession,
	encodeSession,
	isRestorableUrl,
	prepareSession,
	type SessionTab,
} from './session'

function rec(url: string, changes: Partial<SessionTab> = {}): SessionTab {
	return { url, title: 't', pinned: false, active: false, ...changes }
}

describe('restorable urls', () => {
	it.each(['http://a.com', 'https://a.com/x?y=1'])('%s is restorable', (url) => {
		expect(isRestorableUrl(url)).toBe(true)
	})
	it.each([
		['start page', 'zing://home'],
		['blank tab', 'about:blank'],
		['file', 'file:///etc/passwd'],
		['junk', 'not a url'],
		['empty', ''],
	])('%s is not restorable', (_name, url) => {
		expect(isRestorableUrl(url)).toBe(false)
	})
})

describe('prepareSession', () => {
	it('respects the cap and keeps every pinned tab through it', () => {
		const loose = Array.from({ length: 60 }, (_, i) => rec(`https://loose${i}.com`))
		const pinned = Array.from({ length: 5 }, (_, i) => rec(`https://pinned${i}.com`, { pinned: true }))
		const capped = prepareSession([...loose, ...pinned])
		expect(capped).toHaveLength(MAX_SESSION_TABS)
		expect(capped.filter((t) => t.pinned)).toHaveLength(5)
	})

	it('leaves order alone under the cap', () => {
		const tabs = [rec('https://b.com'), rec('https://a.com')]
		expect(prepareSession(tabs)).toEqual(tabs)
	})
})

describe('round trip', () => {
	const session = [
		rec('https://a.com', { pinned: true }),
		rec('https://b.com', { active: true }),
		rec('zing://home'),
	]

	it('filters unrestorable tabs on save and remembers pinned and active', () => {
		const back = decodeSession(encodeSession(session))
		expect(back).toHaveLength(2)
		expect(back[0]?.pinned).toBe(true)
		expect(back.some((t) => t.active && t.url === 'https://b.com')).toBe(true)
	})

	it('decodes garbage and empty input to no tabs', () => {
		expect(decodeSession('{{{not json')).toEqual([])
		expect(decodeSession('')).toEqual([])
		expect(decodeSession('{"url":"https://a.com"}')).toEqual([])
	})

	it('drops malformed entries but keeps valid ones', () => {
		const text = JSON.stringify([{ url: 'https://a.com' }, 7, rec('https://ok.com')])
		expect(decodeSession(text).map((t) => t.url)).toEqual(['https://ok.com'])
	})
})
