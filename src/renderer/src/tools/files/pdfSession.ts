// How the PDF tools read a file and save a result: through a read session on the viewer file server (https://view.zing), not
// through the JSON bridge. The host judges the file's first bytes and size when it opens the session, streams it to a fetch,
// and takes the result as a streamed PUT into Downloads under a numbered name. A panel's sessions end when the panel closes.
import type { PickedFile } from '@shared/engines/engineJob'
import type { ViewerOpened } from '@shared/convert/viewable'
import { PDF_LIMITS, PdfEditError } from '@shared/pdf/pdfCore'
import { saveUrl } from './bgremoveProtocol'

export interface SessionDeps {
	open(panelId: string, token: string, kinds: readonly ['pdf', 'image']): Promise<ViewerOpened>
	fetchFn: typeof fetch
}

const KINDS = ['pdf', 'image'] as const

export interface ReadFile {
	readonly bytes: Uint8Array
	/** The session the file came through; results are saved into it. */
	readonly sessionId: string
}

/** Opens `token` for this panel (PDF and pictures only; the host refuses anything else) and reads it. */
export async function readSession(deps: SessionDeps, panelId: string, token: string, signal?: AbortSignal): Promise<ReadFile> {
	const opened = await deps.open(panelId, token, KINDS)
	const response = await deps.fetchFn(opened.url, signal === undefined ? undefined : { signal })
	if (!response.ok) throw new PdfEditError(`${opened.name} couldn't be read (the file server answered ${response.status}).`)
	// The host has already capped the file; this guards the copy that is about to be held in memory.
	const declared = Number(response.headers.get('content-length') ?? 0)
	if (declared > PDF_LIMITS.maxReadBytes * 2) throw new PdfEditError(`${opened.name} is larger than the PDF tools read.`)
	return { bytes: new Uint8Array(await response.arrayBuffer()), sessionId: opened.id }
}

/** Streams `bytes` into Downloads through the session; answers the name the host gave it and a token. */
export async function putResult(deps: SessionDeps, sessionId: string, name: string, bytes: Uint8Array, signal?: AbortSignal): Promise<PickedFile> {
	if (bytes.length > PDF_LIMITS.maxSaveBytes) {
		throw new PdfEditError(`The result is ${Math.ceil(bytes.length / (1024 * 1024))} MB; the PDF tools save files up to ${PDF_LIMITS.maxSaveBytes / (1024 * 1024)} MB.`)
	}
	const response = await deps.fetchFn(saveUrl(sessionId, name), { method: 'PUT', body: bytes as BodyInit, ...(signal === undefined ? {} : { signal }) })
	if (response.status !== 201) {
		const reason = (await response.text()).trim()
		throw new PdfEditError(`${name} wasn't saved (${response.status}${reason === '' ? '' : `: ${reason}`}).`)
	}
	return (await response.json()) as PickedFile
}
