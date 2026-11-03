import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, type Locator, type Page } from '@playwright/test'
import type { ZingApi } from '../src/shared/ipc'
import { launchZing, pageWhere, test, type Launched } from './zing'

declare const window: { readonly zing: ZingApi }

/** Opens the Toolbox, picks a tile, and returns the floating panel the tool lives in. */
async function openTool(zing: Launched, title: string): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').filter({ hasText: title }).click()
	const panel = surface.getByRole('dialog', { name: title })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

const result = (panel: Locator, label: string): Locator => panel.getByRole('group', { name: label, exact: true })
const base64url = (text: string): string => Buffer.from(text).toString('base64url')

test('JSON formats {"a":1} and a bad document shows the line of the error', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'JSON / YAML')
	await panel.getByRole('textbox', { name: 'JSON input' }).fill('{"a":1}')
	await expect(panel.getByRole('textbox', { name: 'JSON output' })).toHaveValue('{\n  "a": 1\n}')
	await panel.getByRole('textbox', { name: 'JSON input' }).fill('{\n\t"a": 1,\n\t"b": }')
	await expect(panel.getByRole('alert')).toContainText('Line 3')
	await zing.close()
})

test('Base64 round-trips URL-safe input without padding', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Base64')
	await panel.getByRole('radio', { name: 'Decode' }).click()
	await panel.getByRole('textbox', { name: 'Base64 input' }).fill('Pz8-Pg')
	await expect(panel.getByRole('textbox', { name: 'Base64 output' })).toHaveValue('??>>')
	await panel.getByRole('radio', { name: 'Encode' }).click()
	await panel.getByRole('textbox', { name: 'Base64 input' }).fill('??>>')
	await expect(panel.getByRole('textbox', { name: 'Base64 output' })).toHaveValue('Pz8+Pg==')
	await zing.close()
})

test('SHA-256 of abc, and of a file', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Hashes')
	await panel.getByRole('textbox', { name: 'Text to hash' }).fill('abc')
	await expect(result(panel, 'Digest')).toContainText('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
	await panel.getByRole('radio', { name: 'MD5' }).click()
	await expect(result(panel, 'Digest')).toContainText('900150983cd24fb0d6963f7d28e17f72')
	await panel.getByRole('radio', { name: 'SHA-256' }).click()

	const dir = mkdtempSync(join(tmpdir(), 'zing-e2e-hash-'))
	try {
		const file = join(dir, 'abc.txt')
		writeFileSync(file, 'abc')
		await panel.getByRole('textbox', { name: 'File path' }).fill(file)
		await panel.getByRole('button', { name: 'Hash file' }).click()
		await expect(result(panel, 'File digest')).toContainText('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
		await panel.getByRole('textbox', { name: 'File path' }).fill(join(dir, 'missing.bin'))
		await panel.getByRole('button', { name: 'Hash file' }).click()
		await expect(panel.getByRole('alert')).toContainText(/find|exist|not found/i)
	} finally {
		rmSync(dir, { recursive: true, force: true })
	}
	await zing.close()
})

test('HMAC-SHA-256 of the quick brown fox', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'HMAC')
	await panel.getByRole('textbox', { name: 'Secret key' }).fill('key')
	await panel.getByRole('textbox', { name: 'Message' }).fill('The quick brown fox jumps over the lazy dog')
	await expect(result(panel, 'HMAC-SHA-256')).toContainText('f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8')
	await zing.close()
})

test('a JWT decodes its payload', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'JWT Decoder')
	const token = [base64url('{"alg":"HS256","typ":"JWT"}'), base64url('{"name":"Ada","sub":"123"}'), 'c2ln'].join('.')
	await panel.getByRole('textbox', { name: 'JWT' }).fill(token)
	await expect(result(panel, 'Payload')).toContainText('"name": "Ada"')
	await expect(result(panel, 'Header')).toContainText('"alg": "HS256"')
	await zing.close()
})

test('a cron line shows its next run', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Cron Parser')
	await panel.getByRole('textbox', { name: 'Cron expression' }).fill('*/15 * * * *')
	await expect(panel.getByText('every 15 minutes')).toBeVisible()
	await expect(result(panel, 'Next run 1')).toContainText(/\d/)
	await expect(result(panel, 'Next run 3')).toContainText(/\d/)
	await panel.getByRole('textbox', { name: 'Cron expression' }).fill('0 0 31 2 *')
	await expect(panel.getByRole('alert')).toBeVisible()
	await zing.close()
})

test('a catastrophic regex gives up after 2 seconds and the tool still responds', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Regex Tester')
	await panel.getByRole('textbox', { name: 'Test string' }).fill('a'.repeat(40) + '!')
	await panel.getByRole('textbox', { name: 'Pattern' }).fill('(a+)+$')
	await expect(panel.getByRole('alert')).toContainText('gave up after 2 seconds', { timeout: 10_000 })
	await panel.getByRole('textbox', { name: 'Test string' }).fill('aaa')
	await panel.getByRole('textbox', { name: 'Pattern' }).fill('a')
	await expect(panel.getByText('3 matches')).toBeVisible()
	await zing.close()
})

test('a 5 MB JSON paste does not stop the chrome answering getTabs', async () => {
	const zing = await launchZing()
	const { surface, panel } = await openTool(zing, 'JSON / YAML')
	const row = JSON.stringify({ id: 1234567, name: 'row of padding text', tags: ['alpha', 'beta', 'gamma'], nested: { ok: true, value: 3.14159 } })
	const big = '[' + Array.from({ length: Math.ceil((5 * 1024 * 1024) / (row.length + 1)) }, () => row).join(',') + ']'
	expect(big.length).toBeGreaterThan(5 * 1024 * 1024)

	const input = panel.getByRole('textbox', { name: 'JSON input' })
	const pasted = input.fill(big)
	const slowest = { chrome: 0, surface: 0 }
	for (let i = 0; i < 8; i++) {
		const chromeStart = Date.now()
		await zing.chrome.evaluate(() => window.zing.getTabs())
		slowest.chrome = Math.max(slowest.chrome, Date.now() - chromeStart)
		const surfaceStart = Date.now()
		await surface.evaluate(() => 1)
		slowest.surface = Math.max(slowest.surface, Date.now() - surfaceStart)
	}
	await pasted
	expect(slowest.chrome).toBeLessThan(1000)
	await expect(panel.getByRole('textbox', { name: 'JSON output' })).not.toHaveValue('', { timeout: 30_000 })
	await expect(panel.getByRole('alert')).toHaveCount(0)
	await zing.close()
})

test('Timestamps reads a Unix time', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'Timestamps')
	await panel.getByRole('textbox', { name: 'Timestamp' }).fill('1753800000')
	await expect(result(panel, 'ISO 8601 (UTC)')).toContainText('2025-07-29T14:40:00Z')
	await zing.close()
})

test('URL Encoding escapes reserved characters', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'URL Encoding')
	await panel.getByRole('textbox', { name: 'URL input' }).fill('a b&c=d/é')
	await expect(panel.getByRole('textbox', { name: 'URL output' })).toHaveValue('a%20b%26c%3Dd%2F%C3%A9')
	await zing.close()
})

test('UUID generates five and can lowercase them', async () => {
	const zing = await launchZing()
	const { panel } = await openTool(zing, 'UUID')
	await panel.getByRole('button', { name: 'Generate 5' }).click()
	await expect(panel.getByTestId('uuid-row')).toHaveCount(5)
	await expect(panel.getByTestId('uuid-row').first()).toHaveText(/^[0-9A-F]{8}-[0-9A-F]{4}-4[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$/)
	await panel.getByRole('switch', { name: 'Lowercase' }).click()
	await expect(panel.getByTestId('uuid-row').first()).toHaveText(/^[0-9a-f-]{36}$/)
	await zing.close()
})
