import { mkdtemp, rm, stat, utimes } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { AdBlocker, BlocklistFetchError, CorruptEngineCacheError, parseEngine, strictFetch } from './adblock'
import { writeFile } from 'node:fs/promises'

const FILTERS = ['||tracker.example^', '||ads.example/banner$image', '@@||tracker.example/allowed.js'].join('\n')

const page = 'https://news.test/story'

let dir: string
beforeEach(async () => {
	dir = await mkdtemp(join(tmpdir(), 'zing-adblock-'))
})
afterEach(async () => {
	await rm(dir, { recursive: true, force: true })
})

function blocker(over: Partial<ConstructorParameters<typeof AdBlocker>[0]> = {}): AdBlocker {
	return new AdBlocker({
		cachePath: join(dir, 'engine.bin'),
		fetch: async () => {
			throw new Error('offline')
		},
		isEnabled: () => true,
		...over,
	})
}

describe('the engine', () => {
	it('blocks a known tracker and allows a normal URL, offline', () => {
		const b = blocker()
		b.useEngine(parseEngine(FILTERS))
		expect(b.shouldBlock({ url: 'https://tracker.example/collect?id=1', sourceUrl: page, type: 'script' })).toBe(true)
		expect(b.shouldBlock({ url: 'https://ads.example/banner', sourceUrl: page, type: 'image' })).toBe(true)
		expect(b.shouldBlock({ url: 'https://news.test/app.js', sourceUrl: page, type: 'script' })).toBe(false)
	})

	it('honours exception rules', () => {
		const b = blocker()
		b.useEngine(parseEngine(FILTERS))
		expect(b.shouldBlock({ url: 'https://tracker.example/allowed.js', sourceUrl: page, type: 'script' })).toBe(false)
	})

	it('blocks nothing when blockTrackers is off', () => {
		let enabled = true
		const b = blocker({ isEnabled: () => enabled })
		b.useEngine(parseEngine(FILTERS))
		const request = { url: 'https://tracker.example/x', sourceUrl: page, type: 'script' } as const
		expect(b.shouldBlock(request)).toBe(true)
		enabled = false
		expect(b.shouldBlock(request)).toBe(false)
	})

	it('blocks nothing before any engine has loaded', () => {
		const b = blocker()
		expect(b.ready).toBe(false)
		expect(b.shouldBlock({ url: 'https://tracker.example/x', sourceUrl: page, type: 'script' })).toBe(false)
	})
})

describe('the cache and refresh', () => {
	const lists = (text: string) =>
		strictFetch(async () => new Response(text, { status: 200 }))

	it('reports no cache before the first refresh', async () => {
		expect(await blocker().loadCache()).toBe(false)
	})

	it('refreshes from the lists, writes the cache, and a fresh blocker loads it without the network', async () => {
		const first = blocker({ fetch: lists(FILTERS), listUrls: ['https://lists.test/a.txt'] })
		expect(await first.refreshIfStale()).toBe(true)
		expect(first.shouldBlock({ url: 'https://tracker.example/x', sourceUrl: page, type: 'script' })).toBe(true)

		const second = blocker()
		expect(await second.loadCache()).toBe(true)
		expect(second.shouldBlock({ url: 'https://tracker.example/x', sourceUrl: page, type: 'script' })).toBe(true)
		expect(second.shouldBlock({ url: 'https://news.test/app.js', sourceUrl: page, type: 'script' })).toBe(false)
	})

	it('does not refresh again within a day, but does after', async () => {
		let downloads = 0
		const counting = strictFetch(async () => {
			downloads++
			return new Response(FILTERS, { status: 200 })
		})
		const options = { fetch: counting, listUrls: ['https://lists.test/a.txt'] }
		expect(await blocker(options).refreshIfStale()).toBe(true)
		expect(await blocker(options).refreshIfStale()).toBe(false)
		expect(downloads).toBe(1)

		const old = new Date(Date.now() - 25 * 60 * 60 * 1000)
		await utimes(join(dir, 'engine.bin'), old, old)
		expect(await blocker(options).refreshIfStale()).toBe(true)
		expect(downloads).toBe(2)
		expect((await stat(join(dir, 'engine.bin'))).size).toBeGreaterThan(0)
	})

	it('shares one download between overlapping refreshes', async () => {
		let downloads = 0
		const slow = strictFetch(async () => {
			downloads++
			return new Response(FILTERS, { status: 200 })
		})
		const b = blocker({ fetch: slow, listUrls: ['https://lists.test/a.txt'] })
		await Promise.all([b.refreshIfStale(), b.refreshIfStale()])
		expect(downloads).toBe(1)
	})

	it('raises a specific error for a failed download and leaves any cache alone', async () => {
		const failing = strictFetch(async () => new Response('nope', { status: 503 }))
		const b = blocker({ fetch: failing, listUrls: ['https://lists.test/a.txt'] })
		await expect(b.refreshIfStale()).rejects.toBeInstanceOf(BlocklistFetchError)
		expect(await blocker().loadCache()).toBe(false)
	})

	it('raises a specific error for an unreadable cache', async () => {
		await writeFile(join(dir, 'engine.bin'), new Uint8Array([1, 2, 3]))
		await expect(blocker().loadCache()).rejects.toBeInstanceOf(CorruptEngineCacheError)
	})
})
