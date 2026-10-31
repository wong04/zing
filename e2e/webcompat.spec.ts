// Web compatibility against live sites. Opt-in, because it needs the internet
// and the sites change: `ZING_WEBCOMPAT=1 npm run test:e2e -- webcompat`.
// It never signs in or types anything into a site; it checks what Zing looks
// like to them and that their pages load as they would in Chrome.
import { expect, test } from '@playwright/test'
import { activeTabId, navigateTab, pageAt } from './fixtures'
import { launchZing } from './zing'

test.skip(process.env['ZING_WEBCOMPAT'] !== '1', 'live-site checks; set ZING_WEBCOMPAT=1')

interface Identity {
	userAgent: string
	brands: string[]
	fullVersionList: string[]
}

async function identityOf(app: Awaited<ReturnType<typeof launchZing>>['app'], url: string): Promise<Identity> {
	const page = await pageAt(app, url)
	return page.evaluate(async () => {
		const data = (navigator as Navigator & { userAgentData?: { brands: { brand: string; version: string }[]; getHighEntropyValues(h: string[]): Promise<{ fullVersionList?: { brand: string; version: string }[] }> } }).userAgentData
		const high = data ? await data.getHighEntropyValues(['fullVersionList']) : {}
		return {
			userAgent: navigator.userAgent,
			brands: (data?.brands ?? []).map((b) => `${b.brand} ${b.version}`),
			fullVersionList: (high.fullVersionList ?? []).map((b) => `${b.brand} ${b.version}`),
		}
	})
}

test('Zing presents itself as Chrome, not Electron', async () => {
	const { app, chrome } = await launchZing()
	await navigateTab(chrome, await activeTabId(chrome), 'https://www.google.com/')
	const identity = await identityOf(app, 'https://www.google.com/')
	console.info(JSON.stringify(identity, null, 2))
	const everything = [identity.userAgent, ...identity.brands, ...identity.fullVersionList].join(' | ')
	expect(everything).not.toMatch(/electron|zing/i)
	expect(identity.userAgent).toMatch(/Chrome\/\d+/)
	expect(identity.brands.some((b) => b.startsWith('Chromium '))).toBe(true)
	await app.close()
})

test('Google sign-in loads its email step without the "not secure" block', async () => {
	const { app, chrome } = await launchZing()
	await navigateTab(chrome, await activeTabId(chrome), 'https://accounts.google.com/')
	const page = await pageAt(app, 'https://accounts.google.com/')
	// Google's "Email or phone" box is a text input named identifier, not type=email.
	await expect(page.locator('input[name="identifier"]')).toBeVisible({ timeout: 30_000 })
	const text = await page.locator('body').innerText()
	console.info(`landed on ${page.url()}`)
	expect(text).not.toMatch(/may not be secure|browser or app may not be secure|try using a different browser/i)
	await app.close()
})
