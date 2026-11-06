import { describe, expect, it } from 'vitest'
import { PDF_LIMITS, PdfEditError } from '@shared/pdf/pdfCore'
import { openSaveSession, putResult, readSession, type SessionDeps } from './pdfSession'

function deps(respond: (url: string, init?: RequestInit) => Response | Promise<Response>, calls: { url: string; init?: RequestInit }[] = []): SessionDeps {
	return {
		open: async (_panel, token) => ({ id: `session-${token}`, name: 'a.pdf', url: `https://view.zing/session-${token}`, show: 'native' }) as never,
		openSave: async (_panel, kinds) => ({ id: `save-${kinds.join('-')}`, name: 'results', url: 'https://view.zing/x', show: 'native' }) as never,
		fetchFn: (async (url: string, init?: RequestInit) => {
			calls.push({ url, init })
			return respond(url, init)
		}) as typeof fetch,
	}
}

describe('reading a granted file through a session', () => {
	it('opens a pdf and image session for the panel and fetches its address', async () => {
		const calls: { url: string; init?: RequestInit }[] = []
		const opened: string[][] = []
		const d = deps(() => new Response(new Uint8Array([1, 2, 3])), calls)
		const read = await readSession({ ...d, open: async (panel, token, kinds) => (opened.push([panel, token, ...kinds]), d.open(panel, token, kinds)) }, 'panel-1', 'tok')
		expect([...read.bytes]).toEqual([1, 2, 3])
		expect(read.sessionId).toBe('session-tok')
		expect(opened).toEqual([['panel-1', 'tok', 'pdf', 'image']])
		expect(calls[0]!.url).toBe('https://view.zing/session-tok')
	})
	it('refuses an answer that is not a file, naming the status', async () => {
		await expect(readSession(deps(() => new Response('gone', { status: 404 })), 'p', 't')).rejects.toThrow(/404/)
	})
	it('refuses an answer longer than the read limit before it is held', async () => {
		const huge = new Response(new Uint8Array(1), { headers: { 'content-length': String(PDF_LIMITS.maxReadBytes * 3) } })
		await expect(readSession(deps(() => huge), 'p', 't')).rejects.toThrow(PdfEditError)
	})
})

describe('a session for saving', () => {
	it('is opened from the panel and the kinds alone, with no file', async () => {
		expect(await openSaveSession(deps(() => new Response('')), 'panel-1')).toBe('save-pdf-image')
	})
})

describe('saving a result', () => {
	it('PUTs the bytes to the session under the name and answers the name and token', async () => {
		const calls: { url: string; init?: RequestInit }[] = []
		const out = await putResult(deps(() => new Response(JSON.stringify({ name: 'a 2.pdf', token: 'abc' }), { status: 201 }), calls), 's1', 'a #1.pdf', new Uint8Array([9]))
		expect(out).toEqual({ name: 'a 2.pdf', token: 'abc' })
		expect(calls[0]!.url).toBe('https://view.zing/save/s1/a%20%231.pdf')
		expect(calls[0]!.init?.method).toBe('PUT')
	})
	it('says the host reason for each refusal', async () => {
		for (const [status, text] of [[400, 'bad bytes'], [404, ''], [408, 'stalled'], [413, 'too big'], [500, 'disk'], [503, 'locked'], [507, 'quota used up']] as const) {
			const error = await putResult(deps(() => new Response(text, { status })), 's', 'a.pdf', new Uint8Array([1])).catch((e: unknown) => e)
			expect(error, String(status)).toBeInstanceOf(PdfEditError)
			expect((error as Error).message, String(status)).toContain(String(status))
			if (text) expect((error as Error).message).toContain(text)
		}
	})
	it('refuses a result over the save limit before sending a byte', async () => {
		const calls: { url: string; init?: RequestInit }[] = []
		await expect(putResult(deps(() => new Response('x'), calls), 's', 'a.pdf', { length: PDF_LIMITS.maxSaveBytes + 1 } as Uint8Array)).rejects.toThrow(PdfEditError)
		expect(calls).toHaveLength(0)
	})
	it('hands a signal to the request so Cancel can end it', async () => {
		const calls: { url: string; init?: RequestInit }[] = []
		const signal = new AbortController().signal
		await putResult(deps(() => new Response(JSON.stringify({ name: 'a.pdf', token: 't' }), { status: 201 }), calls), 's', 'a.pdf', new Uint8Array([1]), signal)
		expect(calls[0]!.init?.signal).toBe(signal)
	})
})
