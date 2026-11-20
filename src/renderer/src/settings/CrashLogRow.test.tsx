import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { CrashLogRowView } from './CrashLogRow'

vi.mock('../api', () => ({ zing: { getCrashLog: vi.fn(), openCrashLog: vi.fn(), clearCrashLog: vi.fn() } }))

const render = (exists: boolean | undefined, error?: string) =>
	renderToStaticMarkup(<CrashLogRowView exists={exists} error={error} onOpen={() => {}} onClear={() => {}} />)

describe('the Crash log row', () => {
	it('offers Open crash log and Clear crash log once a crash is logged, and says the log stays on this PC', () => {
		const html = render(true)
		expect(html).toContain('Open crash log')
		expect(html).toContain('Clear crash log')
		expect(html).toContain('Kept on this PC and never sent.')
	})

	it('says no crashes are logged, with no buttons, while there is no log', () => {
		const html = render(false)
		expect(html).toContain('No crashes logged.')
		expect(html).not.toContain('Open crash log')
	})

	it('shows nothing until it knows', () => {
		expect(render(undefined)).toBe('')
	})

	it('says why the log could not be opened or cleared', () => {
		expect(render(true, 'access denied')).toContain('access denied')
	})
})
