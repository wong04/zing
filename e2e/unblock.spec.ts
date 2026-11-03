import { createServer, type IncomingMessage, type Server } from 'node:http'
import { createServer as createSocketServer, type AddressInfo } from 'node:net'
import { expect, type Locator, type Page } from '@playwright/test'
import { launchZing, pageWhere, test, type Launched } from './zing'

// No test here reaches the internet: the host asks a DoH server this file runs on 127.0.0.1, and the probes find what the
// test says (ZING_E2E_PROBES, see Zing.Host/TestHooks.NetServices).

interface World {
	readonly system: readonly string[]
	readonly tcp: boolean
	readonly tlsValid: boolean
}

interface LocalDoh {
	readonly url: string
	readonly asked: string[]
	close(): Promise<void>
}

async function serveDoh(answer: readonly string[]): Promise<LocalDoh> {
	const asked: string[] = []
	const server: Server = createServer((request: IncomingMessage, response) => {
		const name = new URL(request.url ?? '/', 'http://127.0.0.1').searchParams.get('name') ?? ''
		asked.push(name)
		response.setHeader('Content-Type', 'application/dns-json')
		response.end(JSON.stringify({ Status: 0, Answer: answer.map((data) => ({ name, type: 1, data })) }))
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	const { port } = server.address() as AddressInfo
	return { url: `http://127.0.0.1:${port}/dns-query`, asked, close: () => new Promise((resolve) => server.close(() => resolve())) }
}

/** An address nothing listens on, standing in for a DoH service the network blocks. */
async function deadAddress(): Promise<string> {
	const probe = createSocketServer()
	await new Promise<void>((resolve) => probe.listen(0, '127.0.0.1', resolve))
	const { port } = probe.address() as AddressInfo
	await new Promise<void>((resolve) => probe.close(() => resolve()))
	return `http://127.0.0.1:${port}/dns-query`
}

async function launchWith(dohUrl: string, world: World): Promise<Launched> {
	return launchZing({ env: { ZING_E2E_DOH_URL: dohUrl, ZING_E2E_PROBES: JSON.stringify(world) } })
}

async function openDiagnose(zing: Launched): Promise<{ surface: Page; panel: Locator }> {
	await zing.chrome.getByRole('button', { name: 'Toolbox' }).first().click()
	const surface = await pageWhere(zing.browser, (p) => p.url().endsWith('#surface'))
	await expect(surface.getByRole('dialog', { name: 'Toolbox' })).toBeVisible()
	await surface.getByTestId('toolbox-tile').filter({ hasText: 'Diagnose' }).click()
	const panel = surface.getByRole('dialog', { name: 'Diagnose' })
	await expect(panel).toBeVisible()
	return { surface, panel }
}

const steps = (panel: Locator): Locator => panel.getByRole('textbox', { name: 'Steps' })

async function diagnose(panel: Locator, site: string): Promise<void> {
	await panel.getByRole('textbox', { name: 'Site' }).fill(site)
	await panel.getByRole('button', { name: 'Diagnose', exact: true }).click()
}

test('Diagnose reports a local server as not blocked', async () => {
	const doh = await serveDoh(['93.184.216.34'])
	const zing = await launchWith(doh.url, { system: ['93.184.216.34'], tcp: true, tlsValid: true })
	const { panel } = await openDiagnose(zing)
	await diagnose(panel, 'local.test')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('Not blocked')
	await expect(panel.getByRole('group', { name: 'Network DNS' })).toContainText('93.184.216.34')
	await expect(panel.getByRole('group', { name: 'Public DNS' })).toContainText('93.184.216.34')
	await expect(steps(panel)).toHaveValue(/Your network's address serves a valid certificate for local\.test/)
	// The host really asked the DoH server over HTTP, for the name that was typed.
	expect(doh.asked).toEqual(['local.test'])
	// Nothing to get around, so no method is offered.
	await expect(panel.getByRole('button', { name: /in Open a Link/ })).toHaveCount(0)
	await zing.close()
	await doh.close()
})

test('a DNS block shows the advice and a button that opens the recommended method', async () => {
	const doh = await serveDoh(['1.2.3.4'])
	const zing = await launchWith(doh.url, { system: ['10.0.0.1'], tcp: true, tlsValid: true })
	const { surface, panel } = await openDiagnose(zing)
	await diagnose(panel, 'https://Blocked.test/some/page')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('DNS-level block')
	await expect(panel.getByTestId('diagnose-detail')).toContainText('DoH + IP pinning')
	await expect(panel.getByTestId('diagnose-recommended')).toHaveText('DoH + IP pinning, Reader relay')
	expect(doh.asked).toEqual(['blocked.test'])
	await panel.getByRole('button', { name: 'Open DoH + IP pinning in Open a Link' }).click()
	// Open a Link arrives in a later task; its panel shows the empty state until then.
	await expect(surface.getByRole('dialog', { name: 'Open a Link' })).toBeVisible()
	await zing.close()
	await doh.close()
})

test('encrypted DNS that the network blocks is explained in the report, not shown as an error', async () => {
	const zing = await launchWith(await deadAddress(), { system: ['1.2.3.4'], tcp: false, tlsValid: false })
	const { panel } = await openDiagnose(zing)
	await diagnose(panel, 'local.test')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('Inconclusive')
	await expect(steps(panel)).toHaveValue(/Public DoH: couldn't be used/)
	await expect(steps(panel)).toHaveValue(/block or rewrite encrypted DNS/)
	await expect(panel.getByRole('alert')).toHaveCount(0)
	await zing.close()
})

test('not blocked without encrypted DNS says the verdict rests on the certificate', async () => {
	const zing = await launchWith(await deadAddress(), { system: ['1.2.3.4'], tcp: true, tlsValid: true })
	const { panel } = await openDiagnose(zing)
	await diagnose(panel, 'local.test')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('Not blocked')
	await expect(panel.getByTestId('diagnose-caveat')).toContainText("Encrypted DNS couldn't be checked on this network")
	await zing.close()
})

test('a verdict that used encrypted DNS carries no caveat', async () => {
	const doh = await serveDoh(['93.184.216.34'])
	const zing = await launchWith(doh.url, { system: ['93.184.216.34'], tcp: true, tlsValid: true })
	const { panel } = await openDiagnose(zing)
	await diagnose(panel, 'local.test')
	await expect(panel.getByTestId('diagnose-verdict')).toHaveText('Not blocked')
	await expect(panel.getByTestId('diagnose-caveat')).toHaveCount(0)
	await zing.close()
	await doh.close()
})

test('something that is not a site name is refused with a reason', async () => {
	const doh = await serveDoh(['1.2.3.4'])
	const zing = await launchWith(doh.url, { system: [], tcp: false, tlsValid: false })
	const { panel } = await openDiagnose(zing)
	await diagnose(panel, 'not a site')
	await expect(panel.getByRole('alert')).toContainText("isn't a site name")
	await expect(panel.getByTestId('diagnose-verdict')).toHaveCount(0)
	expect(doh.asked).toEqual([])
	await zing.close()
	await doh.close()
})
