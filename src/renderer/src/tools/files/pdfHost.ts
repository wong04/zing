import type { PickedFile } from '@shared/engines/engineJob'
import { zing } from '../../api'
import { putResult, readSession, type ReadFile, type SessionDeps } from './pdfSession'

const deps: SessionDeps = { open: (panelId, token, kinds) => zing.openToolSession(panelId, token, kinds), fetchFn: (input, init) => fetch(input, init) }

/** Reads a picked, dropped or saved file through a session of this panel (PDF and pictures only; the host judges the file). */
export const readGranted = (panelId: string, token: string, signal?: AbortSignal): Promise<ReadFile> => readSession(deps, panelId, token, signal)

/** A session to save results into, for a file this panel read before (a refused open ends the panel's earlier session). */
export async function sessionFor(panelId: string, token: string): Promise<string> {
	return (await deps.open(panelId, token, ['pdf', 'image'])).id
}

export const saveResult = (sessionId: string, name: string, bytes: Uint8Array, signal?: AbortSignal): Promise<PickedFile> => putResult(deps, sessionId, name, bytes, signal)
