import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import type { AddressInfo } from 'node:net'
import { expect, type ElectronApplication, type Page } from '@playwright/test'
import type { ZingApi, TabsState } from '../src/shared/ipc'

type Route = (request: IncomingMessage, response: ServerResponse) => void

export interface Fixture {
	readonly port: number
	/** Every request received, as `host path`. */
	readonly hits: string[]
	close(): Promise<void>
}

export function html(body: string): Route {
	return (_req, res) => {
		res.setHeader('content-type', 'text/html')
		res.end(`<!doctype html><title>pending</title>${body}`)
	}
}

export async function serve(routes: Readonly<Record<string, Route>>): Promise<Fixture> {
	const hits: string[] = []
	const server: Server = createServer((req, res) => {
		const url = new URL(req.url ?? '/', 'http://fixture.test')
		hits.push(`${req.headers.host ?? ''} ${url.pathname}`)
		const route = routes[url.pathname]
		if (!route) {
			res.statusCode = 200
			res.setHeader('content-type', 'image/gif')
			res.end()
			return
		}
		route(req, res)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	return {
		port: (server.address() as AddressInfo).port,
		hits,
		close: () => new Promise((resolve) => server.close(() => resolve())),
	}
}

type WithZing = { zing: ZingApi }

export function tabsOf(chrome: Page): Promise<TabsState> {
	return chrome.evaluate(() => (globalThis as unknown as WithZing).zing.getTabs())
}

export async function activeTabId(chrome: Page): Promise<string> {
	const { activeId } = await tabsOf(chrome)
	if (activeId === undefined) throw new Error('no active tab')
	return activeId
}

export function navigateTab(chrome: Page, id: string, url: string): Promise<void> {
	return chrome.evaluate(([tab, target]) => (globalThis as unknown as WithZing).zing.navigateTab(tab!, target!), [id, url])
}

export function closeTab(chrome: Page, id: string): Promise<void> {
	return chrome.evaluate((tab) => (globalThis as unknown as WithZing).zing.closeTab(tab), id)
}

export async function newTab(chrome: Page): Promise<string> {
	await chrome.evaluate(() => (globalThis as unknown as WithZing).zing.runCommand('tab.new'))
	return activeTabId(chrome)
}

/** The Playwright page of a tab whose URL starts with the prefix. */
export async function pageAt(app: ElectronApplication, prefix: string): Promise<Page> {
	await expect.poll(() => app.windows().some((w) => w.url().startsWith(prefix)), { timeout: 15_000 }).toBe(true)
	return app.windows().find((w) => w.url().startsWith(prefix))!
}

/** Waits until the fixture page has run its script and replaced the placeholder title. */
export async function settledTitle(page: Page): Promise<string> {
	await expect.poll(() => page.title(), { timeout: 15_000 }).not.toBe('pending')
	return page.title()
}
