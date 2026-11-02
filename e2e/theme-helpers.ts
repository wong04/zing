// Shared by the theme specs: what a view's own accent is, and waiting until it shows a given one.
import { expect, type Page } from '@playwright/test'
import { DEFAULT_CUSTOM_COLOR } from '../src/shared/settings'
import { accentVariables } from '../src/shared/theme/accentVariables'

/** The globals a view's page has, typed here because these specs do not compile against the DOM lib. */
export interface ViewGlobals {
	document: { documentElement: unknown; querySelector(selector: string): unknown }
	getComputedStyle(element: unknown): { getPropertyValue(name: string): string }
	matchMedia(query: string): { matches: boolean }
}

/** The view's own --accent, and whether the view is in its dark appearance (which the palette depends on). */
export async function viewAccent(view: Page): Promise<{ accent: string; dark: boolean }> {
	return view.evaluate(() => {
		const g = globalThis as unknown as ViewGlobals
		return {
			accent: g.getComputedStyle(g.document.documentElement).getPropertyValue('--accent').trim(),
			dark: g.matchMedia('(prefers-color-scheme: dark)').matches,
		}
	})
}

/** Waits until the view shows the accent that `accent` and `customColor` give in its own appearance. */
export async function expectAccent(view: Page, accent: string, customColor = DEFAULT_CUSTOM_COLOR): Promise<void> {
	const { dark } = await viewAccent(view)
	await expect.poll(async () => (await viewAccent(view)).accent).toBe(accentVariables(accent, customColor, dark)['--accent'])
}
