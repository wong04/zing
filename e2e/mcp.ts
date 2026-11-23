import { existsSync, readFileSync } from 'node:fs'
import { createServer } from 'node:net'
import type { AddressInfo } from 'node:net'
import { join } from 'node:path'
import { expect, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { scratchDir } from './zing'

declare const window: { readonly zing: ZingApi }

export interface ToolContent {
	readonly type: string
	readonly text?: string
	readonly data?: string
	readonly mimeType?: string
}

export interface ToolResult {
	readonly isError?: boolean
	readonly content: readonly ToolContent[]
	readonly structuredContent?: { readonly code?: string }
}

/** An MCP client over Zing's Streamable HTTP endpoint, as Claude Code would be. */
export interface McpClient {
	call(name: string, args?: Record<string, unknown>): Promise<ToolResult>
}

async function freePort(): Promise<number> {
	const probe = createServer()
	await new Promise<void>((resolve) => probe.listen(0, '127.0.0.1', resolve))
	const { port } = probe.address() as AddressInfo
	await new Promise<void>((resolve) => probe.close(() => resolve()))
	return port
}

function post(chrome: Page, message: Record<string, unknown>): Promise<void> {
	return chrome.evaluate((m) => (globalThis as unknown as { chrome: { webview: { postMessage(m: unknown): void } } }).chrome.webview.postMessage({ kind: 'test', ...m }), message)
}

/** Shares a tab with AI without the share UI (TestHooks ai-share). */
export async function shareTab(chrome: Page, tab: string): Promise<void> {
	await post(chrome, { action: 'ai-share', tab })
}

/** The bearer token, as Settings shows it; e2e runs only (TestHooks ai-token). */
async function token(chrome: Page): Promise<string> {
	const file = join(scratchDir('zing-ai-token-'), 'token.json')
	await post(chrome, { action: 'ai-token', file })
	await expect.poll(() => existsSync(file)).toBe(true)
	return (JSON.parse(readFileSync(file, 'utf8')) as { token: string }).token
}

/** Turns AI control on, on a port of this test's own so a real Zing's endpoint is never reached, and opens a session. */
export async function connectMcp(chrome: Page): Promise<McpClient> {
	const port = await freePort()
	await chrome.evaluate((p) => window.zing.setSetting('aiControlPort', p), port)
	await chrome.evaluate(() => window.zing.setSetting('aiControlEnabled', true))
	const bearer = await token(chrome)
	const url = `http://127.0.0.1:${port}/mcp`
	// Set by initialize, then sent with every later request.
	const opened: { session?: string } = {}
	let id = 0

	async function send(body: Record<string, unknown>): Promise<Response> {
		const headers: Record<string, string> = { 'content-type': 'application/json', accept: 'application/json, text/event-stream', authorization: `Bearer ${bearer}` }
		if (opened.session) headers['mcp-session-id'] = opened.session
		const response = await fetch(url, { method: 'POST', headers, body: JSON.stringify({ jsonrpc: '2.0', ...body }) })
		if (!response.ok) throw new Error(`MCP ${String(body.method)} answered ${response.status}`)
		return response
	}

	async function request(method: string, params: Record<string, unknown>): Promise<unknown> {
		const answer = (await (await send({ id: ++id, method, params })).json()) as { result?: unknown; error?: { message: string } }
		if (answer.error) throw new Error(`MCP ${method} failed: ${answer.error.message}`)
		return answer.result
	}

	const initialized = await send({ id: ++id, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'zing-e2e', version: '0' } } })
	opened.session = initialized.headers.get('mcp-session-id') ?? undefined
	if (!opened.session) throw new Error('initialize gave no Mcp-Session-Id')
	await send({ method: 'notifications/initialized' })
	return { call: (name, args = {}) => request('tools/call', { name, arguments: args }) as Promise<ToolResult> }
}

/** The text of a tool result that isn't an error. */
export function textOf(result: ToolResult): string {
	if (result.isError) throw new Error(`The tool failed: ${result.content[0]?.text ?? ''}`)
	return result.content.map((c) => c.text ?? '').join('')
}
