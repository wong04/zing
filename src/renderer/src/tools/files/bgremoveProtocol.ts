/** What the main thread and the background-removal worker say to each other. */

/** The viewer file server (ViewerFiles.Host): the host answers it for Zing's own views only. A worker reads the picture and the model from it and PUTs the result to it. */
export const VIEW_ORIGIN = 'https://view.zing'

export const MODEL_URL = `${VIEW_ORIGIN}/model/bgmodel`

/** Where the result goes: tied to the picture's session, so it stops working when the session is revoked. */
export const saveUrl = (sessionId: string, name: string): string => `${VIEW_ORIGIN}/save/${sessionId}/${encodeURIComponent(name)}`

/** Reads the picture and judges its format and size, without the model: so a picture that cannot be used is refused before the model is offered. */
export interface CheckRequest {
	readonly type: 'check'
	/** The picture's session address on the viewer file server; the host opened it after judging the file's first bytes. */
	readonly fileUrl: string
}

export interface RunRequest {
	readonly type: 'run'
	readonly fileUrl: string
	readonly modelUrl: string
	/** Where the result PNG is PUT. */
	readonly saveUrl: string
}

export type RemoveRequest = CheckRequest | RunRequest

export type RemoveStage = 'reading' | 'decoding' | 'loading-model' | 'running' | 'cutting' | 'saving'

export type FailureKind = 'unsupported' | 'too-large' | 'corrupt' | 'model' | 'unavailable'

export type RemoveMessage =
	| { readonly type: 'progress'; readonly stage: RemoveStage; readonly fraction?: number }
	| { readonly type: 'checked' }
	/** The result is already saved by the host in Downloads, under `name`. */
	| { readonly type: 'done'; readonly name: string; readonly width: number; readonly height: number; readonly seconds: number }
	| { readonly type: 'failed'; readonly kind: FailureKind; readonly message: string }

/** The words shown beside the progress bar for each stage. */
export const STAGE_TEXT: Readonly<Record<RemoveStage, string>> = {
	reading: 'Reading the picture',
	decoding: 'Decoding the picture',
	'loading-model': 'Loading the model',
	running: 'Finding the subject',
	cutting: 'Cutting out the background',
	saving: 'Saving to your Downloads folder',
}
