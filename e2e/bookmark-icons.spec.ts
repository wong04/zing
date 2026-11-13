// B22: a bookmark keeps its site's icon on disk once its page is visited, and never fetches one for a page that wasn't.
// The pictures (ZING_SCREENSHOTS=1) go into docs/screenshots, light and dark.
import { existsSync, mkdirSync, readdirSync, writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { crc32, deflateSync } from 'node:zlib'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { FAVOURITES_ID, PINNED_ID } from '../src/shared/library/library'
import { viewAccent } from './theme-helpers'
import { launchZing, scratchDir, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

const OUT_DIR = join(import.meta.dirname, '..', 'docs', 'screenshots')

/** A 16×16 PNG of one colour, so each site's icon is told apart at a glance. */
function solidPng(r: number, g: number, b: number): Buffer {
	const chunk = (type: string, data: Buffer): Buffer => {
		const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
		const length = Buffer.alloc(4)
		length.writeUInt32BE(data.length)
		const crc = Buffer.alloc(4)
		crc.writeUInt32BE(crc32(body))
		return Buffer.concat([length, body, crc])
	}
	const header = Buffer.alloc(13)
	header.writeUInt32BE(16, 0)
	header.writeUInt32BE(16, 4)
	header.set([8, 2, 0, 0, 0], 8)
	const row = Buffer.from([0, ...Array.from({ length: 16 }, () => [r, g, b]).flat()])
	const pixels = deflateSync(Buffer.concat(Array.from({ length: 16 }, () => row)))
	return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header), chunk('IDAT', pixels), chunk('IEND', Buffer.alloc(0))])
}

const COLOURS: Readonly<Record<string, readonly [number, number, number]>> = {
	mail: [220, 60, 50],
	docs: [40, 110, 230],
	notes: [245, 180, 20],
	music: [40, 170, 90],
}

/** Serves `/<name>` as a page whose icon is `/<name>.png`, and records every request it gets. */
async function siteServer(): Promise<{ server: Server; base: string; requests: string[] }> {
	const requests: string[] = []
	const server = createServer((req, res) => {
		const path = req.url ?? '/'
		requests.push(path)
		const name = path.slice(1).replace(/\.png$/, '')
		if (path.endsWith('.png')) {
			res.setHeader('content-type', 'image/png')
			res.end(solidPng(...(COLOURS[name] ?? [128, 128, 128])))
			return
		}
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>${name}</title><link rel="icon" href="/${name}.png"><body>${name}</body>`)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	return { server, base: `http://127.0.0.1:${(server.address() as AddressInfo).port}`, requests }
}

let visited: Awaited<ReturnType<typeof siteServer>>
let unvisited: Awaited<ReturnType<typeof siteServer>>

test.beforeAll(async () => {
	visited = await siteServer()
	unvisited = await siteServer()
})

test.afterAll(async () => {
	for (const { server } of [visited, unvisited]) await new Promise<void>((resolve) => server.close(() => resolve()))
})

/** A profile whose Favourites hold these links, as an earlier run or an import might have left them. */
function profileWithFavourites(links: readonly { id: string; title: string; url: string }[]): string {
	const dir = scratchDir('zing-bookmark-icons-')
	const library = {
		bookmarks: [
			{ id: FAVOURITES_ID, title: 'Favourites', updatedAt: 1, children: links.map((l) => ({ ...l, updatedAt: 1 })) },
			{ id: PINNED_ID, title: 'Pinned', updatedAt: 1, children: [] },
		],
		readingList: [],
	}
	writeFileSync(join(dir, 'library.json'), JSON.stringify(library))
	return dir
}

const storedIcons = (profile: string): string[] => {
	const dir = join(profile, 'bookmark-icons')
	return existsSync(dir) ? readdirSync(dir) : []
}

/** Loads `url` in the active tab and waits for its icon: each site here has an icon of its own colour, so a new one means the page's arrived. */
async function visit(chrome: Page, url: string): Promise<void> {
	const iconOf = async (id: string): Promise<string | undefined> => (await chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === id)?.icon
	const { activeId } = await chrome.evaluate(() => window.zing.getTabs())
	const before = await iconOf(activeId!)
	await chrome.evaluate(([id, to]) => window.zing.navigateTab(id!, to!), [activeId!, url])
	await expect.poll(async () => {
		const icon = await iconOf(activeId!)
		return icon !== undefined && icon !== before
	}).toBe(true)
}

const ribbon = (chrome: Page, name: string): Locator => chrome.getByRole('list', { name: 'Favourites' }).getByRole('listitem', { name })
const barChip = (chrome: Page, name: string): Locator => chrome.getByRole('toolbar', { name: 'Favourites' }).getByRole('button', { name, exact: true })

test('a visited bookmark keeps its icon on the bar and after a restart; an unvisited one stays blank and is never asked for', async () => {
	const profile = profileWithFavourites([
		{ id: 'visited', title: 'Mail', url: `${visited.base}/mail` },
		{ id: 'unvisited', title: 'Docs', url: `${unvisited.base}/docs` },
	])
	const first = await launchZing({ userDataDir: profile })
	await expect(ribbon(first.chrome, 'Mail').locator('img')).toHaveCount(0)
	await visit(first.chrome, `${visited.base}/mail`)
	await expect.poll(() => storedIcons(profile)).toHaveLength(1)
	await first.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
	await expect(barChip(first.chrome, 'Mail').locator('img')).toHaveAttribute('src', /^data:image\/png;base64,/)
	await expect(barChip(first.chrome, 'Docs').locator('img')).toHaveCount(0)
	await first.close()

	const second = await launchZing({ userDataDir: profile })
	// No tab has the page now: the icon comes from disk.
	await expect(barChip(second.chrome, 'Mail').locator('img')).toHaveAttribute('src', /^data:image\/png;base64,/)
	await expect(barChip(second.chrome, 'Docs')).toHaveText('D')
	await second.close()
	expect(unvisited.requests).toEqual([])
})

test('a page that is not the bookmarked one leaves the bookmark blank', async () => {
	const profile = profileWithFavourites([{ id: 'docs', title: 'Docs', url: `${visited.base}/docs` }])
	const zing = await launchZing({ userDataDir: profile })
	await visit(zing.chrome, `${visited.base}/mail`)
	await visit(zing.chrome, `${visited.base}/docs/elsewhere`)
	await expect(ribbon(zing.chrome, 'Docs').locator('img')).toHaveCount(0)
	expect(storedIcons(profile)).toEqual([])
	await zing.close()
})

test('deleting the bookmark deletes its icon', async () => {
	const profile = profileWithFavourites([{ id: 'mail', title: 'Mail', url: `${visited.base}/mail` }])
	const zing = await launchZing({ userDataDir: profile })
	await visit(zing.chrome, `${visited.base}/mail`)
	await expect.poll(() => storedIcons(profile)).toHaveLength(1)
	await zing.chrome.evaluate(() => window.zing.applyLibrary({ type: 'remove', ids: ['mail'] }))
	await expect.poll(() => storedIcons(profile)).toEqual([])
	await zing.close()
})

test.describe('B22 screenshots', () => {
	test.skip(process.env.ZING_SCREENSHOTS !== '1', 'set ZING_SCREENSHOTS=1 to take the parity pictures')
	test.beforeAll(() => mkdirSync(OUT_DIR, { recursive: true }))

	for (const mode of ['light', 'dark'] as const) {
		test(`the Favourites bar with visited sites' icons, ${mode}`, async () => {
			const names = Object.keys(COLOURS)
			const profile = profileWithFavourites([
				...names.map((name) => ({ id: name, title: name[0]!.toUpperCase() + name.slice(1), url: `${visited.base}/${name}` })),
				{ id: 'never', title: 'Never visited', url: `${unvisited.base}/never` },
			])
			const zing: Launched = await launchZing({ userDataDir: profile })
			for (const name of names) await visit(zing.chrome, `${visited.base}/${name}`)
			await expect.poll(() => storedIcons(profile)).toHaveLength(names.length)
			await zing.chrome.evaluate(() => window.zing.setSetting('tabBarPosition', 'top'))
			await zing.chrome.evaluate((theme) => window.zing.setSetting('theme', theme), mode)
			await zing.chrome.emulateMedia({ colorScheme: mode })
			await expect.poll(async () => (await viewAccent(zing.chrome)).dark).toBe(mode === 'dark')
			const bar = zing.chrome.getByRole('toolbar', { name: 'Favourites' })
			await expect(bar.locator('img')).toHaveCount(names.length)
			await zing.chrome.waitForTimeout(400)
			const box = (await bar.boundingBox())!
			await zing.chrome.screenshot({ path: join(OUT_DIR, `b22-bookmark-icons-${mode}.png`), clip: { x: 0, y: 0, width: Math.min(box.x + box.width, 900), height: box.y + box.height } })
			await zing.close()
		})
	}
})
