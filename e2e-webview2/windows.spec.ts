import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, test, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pages } from './zing'

declare const window: { readonly zing: ZingApi }

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((_req, res) => {
		res.setHeader('content-type', 'text/html')
		res.end('<!doctype html><title>linked</title>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const urls = async (chrome: Page) => (await chrome.evaluate(() => window.zing.getTabs())).tabs.map((t) => t.url)

test('a link in the launch arguments opens in the first window, in the blank tab', async () => {
	const url = `${base}/linked`
	const zing = await launchZing({ args: [url] })
	await expect.poll(() => urls(zing.chrome)).toEqual([url])
	await zing.close()
})

test('a second launch hands its link to the running window as a new tab and exits', async () => {
	const zing = await launchZing()
	await zing.chrome.evaluate(async () => {
		const { activeId } = await window.zing.getTabs()
		await window.zing.navigateTab(activeId!, 'http://127.0.0.1:1/first')
	})
	const url = `${base}/linked?from=other-app`
	expect(await zing.launchAgain([url])).toBe(0)
	await expect.poll(() => urls(zing.chrome)).toContain(url)
	expect(pages(zing.browser).filter((p) => p.url().endsWith('#chrome'))).toHaveLength(1)
	await zing.close()
})

test('a link that is not a web address is ignored', async () => {
	const zing = await launchZing({ args: ['file:///C:/Windows/win.ini', 'ms-settings:'] })
	await expect.poll(() => urls(zing.chrome)).toEqual(['about:blank'])
	await zing.close()
})
