import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { expect, test } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { html, newTab, serve, tabsOf } from './fixtures'
import { launchZing } from './zing'

const page = html('<script>document.title = "linked"</script>')

test('New Window opens a second window and closing the last window quits', async () => {
	const { app, chrome } = await launchZing()
	const count = () => app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length)
	await chrome.evaluate(() => (globalThis as unknown as { zing: ZingApi }).zing.runCommand('window.new'))
	await expect.poll(count).toBe(2)

	// Each window has its own tab service: a tab opened in one does not appear in the other.
	await newTab(chrome)
	expect((await tabsOf(chrome)).tabs).toHaveLength(2)

	await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[1]?.close())
	await expect.poll(count).toBe(1)
	const closed = app.waitForEvent('close')
	await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]?.close())
	await closed
})

test('a link in the launch arguments opens in the first window', async () => {
	const site = await serve({ '/linked': page })
	const url = `http://127.0.0.1:${site.port}/linked`
	const { app, chrome } = await launchZing({ args: [url] })
	await expect.poll(async () => (await tabsOf(chrome)).tabs.map((t) => t.url)).toContain(url)
	await app.close()
	await site.close()
})

test('a second launch hands its link to the running window as a new tab', async () => {
	const site = await serve({ '/linked': page })
	const url = `http://127.0.0.1:${site.port}/linked?from=other-app`
	const { app, chrome, userDataDir } = await launchZing()
	const execPath = await app.evaluate(() => process.execPath)

	const second = spawn(execPath, ['.', `--user-data-dir=${userDataDir}`, url], { stdio: 'ignore' })
	const [code] = await once(second, 'exit')
	expect(code).toBe(0)

	await expect.poll(async () => (await tabsOf(chrome)).tabs.map((t) => t.url)).toContain(url)
	expect(await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length)).toBe(1)
	await app.close()
	await site.close()
})

test('confirm before quit asks only with several tabs open, and Cancel keeps Zing running', async () => {
	const { app, chrome } = await launchZing({ settings: { confirmQuit: true } })
	const asked = () => app.evaluate(() => (globalThis as { __asked?: number }).__asked ?? 0)
	const answer = (response: number) =>
		app.evaluate(
			({ dialog }, r) => {
				const g = globalThis as { __asked?: number }
				g.__asked = 0
				dialog.showMessageBox = (async () => {
					g.__asked = (g.__asked ?? 0) + 1
					return { response: r, checkboxChecked: false }
				}) as unknown as typeof dialog.showMessageBox
			},
			response,
		)

	await newTab(chrome)
	await answer(1)
	await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0]?.close())
	await expect.poll(asked).toBe(1)
	expect(await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length)).toBe(1)

	await app.evaluate(({ app: electronApp }) => electronApp.quit())
	await expect.poll(asked).toBe(2)
	expect(await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length)).toBe(1)

	await answer(0)
	const closed = app.waitForEvent('close')
	await app.evaluate(({ app: electronApp }) => electronApp.quit())
	await closed
})
