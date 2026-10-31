// Port of Krackn `Tests/unit` section M (SessionStore). The interactionState
// cases are WebKit-only and have no Chromium counterpart; Tests/persist drives
// a live WKWebView and is covered by the e2e suite instead.
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import {
	MAX_SESSION_TABS,
	decodeSession,
	encodeSession,
	isRestorableUrl,
	prepareSession,
	type SessionTab,
} from './session'

interface Vectors {
	restorableUrls: { yes: string[]; no: string[] }
	prepare: {
		name: string
		generate?: { loose: number; pinned: number }
		tabs?: SessionTab[]
		expect?: SessionTab[]
		expectLength?: number
		expectPinned?: number
	}[]
	roundTrip: { tabs: SessionTab[]; expect: SessionTab[] }
	decode: { name: string; text: string; expect: SessionTab[] }[]
}

const vectors = JSON.parse(readFileSync(new URL('./session.vectors.json', import.meta.url), 'utf8')) as Vectors

function rec(url: string, changes: Partial<SessionTab> = {}): SessionTab {
	return { url, title: 't', pinned: false, active: false, ...changes }
}

describe('restorable urls', () => {
	it.each(vectors.restorableUrls.yes)('%s is restorable', (url) => {
		expect(isRestorableUrl(url)).toBe(true)
	})
	it.each(vectors.restorableUrls.no)('"%s" is not restorable', (url) => {
		expect(isRestorableUrl(url)).toBe(false)
	})
})

describe('prepareSession', () => {
	it.each(vectors.prepare)('$name', (c) => {
		if (c.generate) {
			const loose = Array.from({ length: c.generate.loose }, (_, i) => rec(`https://loose${i}.com`))
			const pinned = Array.from({ length: c.generate.pinned }, (_, i) => rec(`https://pinned${i}.com`, { pinned: true }))
			const capped = prepareSession([...loose, ...pinned])
			expect(c.expectLength).toBe(MAX_SESSION_TABS)
			expect(capped).toHaveLength(c.expectLength!)
			expect(capped.filter((t) => t.pinned)).toHaveLength(c.expectPinned!)
			return
		}
		expect(prepareSession(c.tabs!)).toEqual(c.expect)
	})
})

describe('round trip', () => {
	it('filters unrestorable tabs on save and remembers pinned and active', () => {
		expect(decodeSession(encodeSession(vectors.roundTrip.tabs))).toEqual(vectors.roundTrip.expect)
	})

	it.each(vectors.decode)('decode: $name', (c) => {
		expect(decodeSession(c.text)).toEqual(c.expect)
	})
})
