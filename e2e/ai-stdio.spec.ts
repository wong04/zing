import { spawn } from 'node:child_process'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { createHash } from 'node:crypto'
import { existsSync } from 'node:fs'
import { createInterface } from 'node:readline'
import { expect } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { connectMcp, shareTab } from './mcp'
import { HOST_EXE, launchZing, test } from './zing'

declare const window: { readonly zing: ZingApi }

// zing-mcp.exe, the Claude desktop app's stdio relay, against a running Zing's pipe.

let server: Server
let page = ''

test.beforeAll(async () => {
	server = createServer((_, res) => {
		res.setHeader('content-type', 'text/html')
		res.end('<!doctype html><title>stdio</title><p>Through the relay</p>')
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	page = `http://127.0.0.1:${(server.address() as AddressInfo).port}/`
})

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())))

const MCP_EXE = HOST_EXE.replace(/Zing\.exe$/, 'zing-mcp.exe')

/** SingleInstance.ProfileKey: what `--profile` takes. */
function profileKey(userData: string): string {
	return 'Zing-' + createHash('sha256').update(userData.toLowerCase(), 'utf8').digest('hex').slice(0, 16).toUpperCase()
}

function relay(key: string) {
	const child = spawn(MCP_EXE, ['--profile', key], { stdio: ['pipe', 'pipe', 'pipe'] })
	const lines: string[] = []
	const waiting: ((line: string) => void)[] = []
	createInterface({ input: child.stdout }).on('line', (line) => {
		const next = waiting.shift()
		if (next) next(line)
		else lines.push(line)
	})
	const exited = new Promise<number | null>((resolve) => child.on('exit', resolve))
	return {
		exited,
		send: (message: object) => child.stdin.write(JSON.stringify(message) + '\n'),
		next: () => new Promise<string>((resolve) => (lines.length > 0 ? resolve(lines.shift()!) : waiting.push(resolve))),
		close: () => child.stdin.end(),
		kill: () => child.kill(),
	}
}

test('zing-mcp.exe is built next to Zing.exe', () => {
	expect(existsSync(MCP_EXE)).toBe(true)
})

test('stdio list_tabs works through zing-mcp.exe', async () => {
	const zing = await launchZing()
	await connectMcp(zing.chrome)
	await zing.chrome.evaluate(() => window.zing.runCommand('tab.new'))
	const { activeId } = await zing.chrome.evaluate(() => window.zing.getTabs())
	await zing.chrome.evaluate(([id, url]) => window.zing.navigateTab(id!, url!), [activeId!, page])
	await expect.poll(async () => (await zing.chrome.evaluate(() => window.zing.getTabs())).tabs.find((t) => t.id === activeId)?.url).toBe(page)
	await shareTab(zing.chrome, activeId!)
	const stdio = relay(profileKey(zing.userDataDir))
	try {
		stdio.send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'zing-e2e', version: '0' } } })
		const initialized = JSON.parse(await stdio.next()) as { id: number; result?: { serverInfo?: unknown } }
		expect(initialized.id).toBe(1)
		expect(initialized.result?.serverInfo).toBeTruthy()
		stdio.send({ jsonrpc: '2.0', method: 'notifications/initialized' })
		await expect
			.poll(async () => {
				stdio.send({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'list_tabs', arguments: {} } })
				const answer = JSON.parse(await stdio.next()) as { result: { isError?: boolean; content: { text: string }[] } }
				return answer.result.isError ? [] : (JSON.parse(answer.result.content[0]!.text) as { url: string }[]).map((t) => t.url)
			})
			.toEqual([page])
	} finally {
		stdio.kill()
		await zing.close()
	}
})

test('zing-mcp.exe says so and exits 1 when Zing has no pipe', async () => {
	const stdio = relay('Zing-0000000000000000')
	const error = JSON.parse(await stdio.next()) as { error: { message: string } }
	expect(error.error.message).toBe("Zing isn't running or AI control is off")
	expect(await stdio.exited).toBe(1)
})
