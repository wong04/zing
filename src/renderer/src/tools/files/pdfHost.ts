import type { PickedFile } from '@shared/engines/engineJob'
import { zing } from '../../api'
import { openSaveSession, putResult, readSession, type ReadFile, type SessionDeps } from './pdfSession'

const deps: SessionDeps = { open: (panelId, token, kinds) => zing.openToolSession(panelId, token, kinds), openSave: (panelId, kinds) => zing.openSaveSession(panelId, kinds), fetchFn: (input, init) => fetch(input, init) }

/** Reads a picked, dropped or saved file through a session of this panel (PDF and pictures only; the host judges the file). */
export const readGranted = (panelId: string, token: string, signal?: AbortSignal): Promise<ReadFile> => readSession(deps, panelId, token, signal)

/** A session to save results into: opened from the panel alone, so a moved or changed source file cannot stop a save. */
export const saveSessionFor = (panelId: string): Promise<string> => openSaveSession(deps, panelId)

export const saveResult = (sessionId: string, name: string, bytes: Uint8Array, signal?: AbortSignal): Promise<PickedFile> => putResult(deps, sessionId, name, bytes, signal)
