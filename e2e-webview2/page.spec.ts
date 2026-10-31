import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, test } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere } from './zing'

declare const window: { readonly zing: ZingApi }

let server: Server
let base = ''

test.beforeAll(async () => {
	server = createServer((req, res) => {
		if (req.url === '/file.bin') {
			res.setHeader('content-type', 'application/octet-stream')
			res.setHeader('content-disposition', 'attachment; filename="report.bin"')
			return void res.end(Buffer.alloc(2048, 7))
		}
		res.setHeader('content-type', 'text/html')
		if (req.url === '/perm') return void res.end('<!doctype html><title>Permissions</title>')
		res.end('<!doctype html><title>Find</title><p>needle one</p><p>needle two</p><p>needle three</p><p>hay</p>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

test('find in page shows a match count and steps through matches', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/find` } })
	await pageWhere(zing.browser, (p) => p.url() === `${base}/find`)
	await zing.chrome.evaluate(() => window.zing.runCommand('page.find'))
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await ui.getByRole('textbox', { name: 'Find in page' }).fill('needle')
	await expect(ui.getByText('1 of 3')).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.runCommand('page.findNext'))
	await expect(ui.getByText('2 of 3')).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.runCommand('page.findPrevious'))
	await expect(ui.getByText('1 of 3')).toBeVisible()
	await zing.chrome.evaluate(() => window.zing.closeFind())
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getPageUi())).find ?? null).toBeNull()
	await zing.close()
})

test('a download lands in the downloads folder, shows in the list and numbers on collision', async () => {
	const zing = await launchZing()
	const directory = join(zing.userDataDir, 'downloads')
	const download = () =>
		zing.chrome.evaluate(async (url) => {
			const { activeId } = await window.zing.getTabs()
			await window.zing.navigateTab(activeId!, url)
		}, `${base}/file.bin`)
	await download()
	await expect.poll(() => existsSync(join(directory, 'report.bin')) && statSync(join(directory, 'report.bin')).size).toBe(2048)
	// Edge's download manager writes the Mark of the Web itself, with the real source.
	const mark = join(directory, 'report.bin:Zone.Identifier')
	await expect.poll(() => existsSync(mark) && readFileSync(mark, 'utf8')).toContain('ZoneId=3')
	expect(readFileSync(mark, 'utf8')).toContain(`HostUrl=${base}/file.bin`)
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByRole('button', { name: 'Show in folder' })).toBeVisible()
	await download()
	await expect.poll(() => readdirSync(directory).sort()).toEqual(['report 2.bin', 'report.bin'])
	await zing.close()
})

test('a permission request is asked in the page UI and denied when Deny is clicked', async () => {
	const zing = await launchZing({ env: { ZING_START_URL: `${base}/perm` } })
	const page = await pageWhere(zing.browser, (p) => p.url() === `${base}/perm`)
	await page.waitForLoadState('domcontentloaded')
	const result = () => page.evaluate(() => (globalThis as unknown as { result?: string }).result)
	await page.evaluate(() => {
		const g = globalThis as unknown as { result?: string; Notification: { requestPermission(): Promise<string> } }
		g.result = 'pending'
		void g.Notification.requestPermission().then((r) => (g.result = r))
	})
	const ui = await pageWhere(zing.browser, (p) => p.url().endsWith('#pageui'))
	await expect(ui.getByText('wants to show notifications')).toBeVisible()
	expect(await result()).toBe('pending')
	await ui.getByRole('button', { name: 'Deny' }).click()
	await expect.poll(result).toBe('denied')
	await zing.close()
})
