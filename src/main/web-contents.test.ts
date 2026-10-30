import type { WebContents } from 'electron'
import { describe, expect, it } from 'vitest'
import { isGoneError, loadUnlessGone, runInPage } from './web-contents'

function fakeContents(destroyed: boolean, run?: () => Promise<unknown>): WebContents {
	return { isDestroyed: () => destroyed, executeJavaScript: run } as unknown as WebContents
}

describe('page-gone failures', () => {
	it('a destroyed page is gone whatever the error says', () => expect(isGoneError(fakeContents(true), new Error('anything'))).toBe(true))
	it('a disposed frame is gone', () => expect(isGoneError(fakeContents(false), new Error('Render frame was disposed before the request completed'))).toBe(true))
	it('any other failure on a live page is a real error', () => expect(isGoneError(fakeContents(false), new Error('SyntaxError: bad script'))).toBe(false))
})

describe('loadUnlessGone', () => {
	it('swallows a load cut short by the view closing', async () => {
		await expect(loadUnlessGone(fakeContents(true), () => Promise.reject(new Error("ERR_FAILED (-2) loading 'file:///x'")))).resolves.toBeUndefined()
	})
	it('rethrows a real load failure on a live view', async () => {
		await expect(loadUnlessGone(fakeContents(false), () => Promise.reject(new Error('ERR_FILE_NOT_FOUND')))).rejects.toThrow('ERR_FILE_NOT_FOUND')
	})
})

describe('runInPage', () => {
	it('returns the value', async () => expect(await runInPage(fakeContents(false, () => Promise.resolve(42)), '42')).toEqual({ status: 'ran', value: 42 }))
	it('reports a page that went away mid-script', async () => {
		const disposed = fakeContents(false, () => Promise.reject(new Error('Render frame was disposed before the request completed')))
		expect(await runInPage(disposed, 'x')).toEqual({ status: 'gone' })
	})
	it('rethrows a script error', async () => {
		await expect(runInPage(fakeContents(false, () => Promise.reject(new Error('ReferenceError: nope'))), 'nope')).rejects.toThrow('ReferenceError')
	})
})
