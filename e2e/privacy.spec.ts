import { expect, test } from '@playwright/test'
import { launchZing } from './zing'
import { activeTabId, closeTab, html, navigateTab, newTab, pageAt, serve, settledTitle, tabsOf } from './fixtures'

const SET = html(`<script>
	localStorage.setItem('k', 'v')
	document.cookie = 'k=v; path=/'
	document.title = 'set:' + localStorage.getItem('k') + ':' + document.cookie
</script>`)

const READ = html(`<script>
	document.title = JSON.stringify({ ls: localStorage.getItem('k'), cookie: document.cookie })
</script>`)

test('a site keeps nothing once its last tab closes', async () => {
	const site = await serve({ '/set': SET, '/read': READ })
	const base = `http://127.0.0.1:${site.port}`
	const { app, chrome } = await launchZing()
	const cookies = () =>
		app.evaluate(async ({ session }) => (await session.fromPartition('zing-private').cookies.get({})).length)

	const first = await activeTabId(chrome)
	await navigateTab(chrome, first, `${base}/set`)
	expect(await settledTitle(await pageAt(app, `${base}/set`))).toBe('set:v:k=v')
	expect(await cookies()).toBe(1)

	// A second tab on the same site keeps its data alive when the first one closes.
	const second = await newTab(chrome)
	await navigateTab(chrome, second, `${base}/read`)
	expect(JSON.parse(await settledTitle(await pageAt(app, `${base}/read`)))).toEqual({ ls: 'v', cookie: 'k=v' })
	await closeTab(chrome, first)
	expect(await cookies()).toBe(1)

	await closeTab(chrome, second)
	await expect.poll(cookies).toBe(0)

	const third = await activeTabId(chrome)
	await navigateTab(chrome, third, `${base}/read?again`)
	const page = await pageAt(app, `${base}/read?again`)
	expect(JSON.parse(await settledTitle(page))).toEqual({ ls: null, cookie: '' })
	await app.close()
	await site.close()
})

test('a request to a blocked host is refused and an ordinary one is not', async () => {
	const site = await serve({
		'/page': (req, res) => {
			const { port } = new URL(`http://${req.headers.host}`)
			html(`<img src="http://blocked-tracker.test:${port}/px">
				<img src="http://friendly.test:${port}/px">`)(req, res)
		},
	})
	const { app, chrome } = await launchZing({
		filters: '||blocked-tracker.test^',
		args: ['--host-resolver-rules=MAP blocked-tracker.test 127.0.0.1, MAP friendly.test 127.0.0.1'],
	})
	await navigateTab(chrome, await activeTabId(chrome), `http://friendly.test:${site.port}/page`)
	await expect
		.poll(() => site.hits.some((h) => h.startsWith(`friendly.test:${site.port} /px`)), { timeout: 15_000 })
		.toBe(true)
	expect(site.hits.filter((h) => h.startsWith('blocked-tracker.test'))).toEqual([])
	await app.close()
	await site.close()
})

test('tracking parameters are stripped from redirects too', async () => {
	const site = await serve({
		'/go': (_req, res) => {
			res.statusCode = 302
			res.setHeader('location', '/landing?utm_source=mail&keep=1')
			res.end()
		},
		'/landing': html('<script>document.title = "landed"</script>'),
	})
	const { app, chrome } = await launchZing()
	const base = `http://127.0.0.1:${site.port}`
	await navigateTab(chrome, await activeTabId(chrome), `${base}/go`)
	await expect
		.poll(async () => (await tabsOf(chrome)).tabs[0]?.url, { timeout: 15_000 })
		.toBe(`${base}/landing?keep=1`)
	await app.close()
	await site.close()
})
