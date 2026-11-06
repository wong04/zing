/** What the main thread and the background-removal worker say to each other. */

/** Reads the picture and judges its format and size, without the model: so a picture that cannot be used is refused before the model is offered. */
export interface CheckRequest {
	readonly type: 'check'
	/** Where the worker fetches the picture from: the host serves a granted file by its token, to Zing's own views only. */
	readonly fileUrl: string
}

export interface RunRequest {
	readonly type: 'run'
	readonly fileUrl: string
	readonly modelUrl: string
}

export type RemoveRequest = CheckRequest | RunRequest

export type RemoveStage = 'reading' | 'decoding' | 'loading-model' | 'running' | 'cutting'

export type FailureKind = 'unsupported' | 'too-large' | 'corrupt' | 'model' | 'unavailable'

export type RemoveMessage =
	| { readonly type: 'progress'; readonly stage: RemoveStage; readonly fraction?: number }
	| { readonly type: 'checked' }
	| { readonly type: 'done'; readonly png: ArrayBuffer; readonly width: number; readonly height: number; readonly seconds: number }
	| { readonly type: 'failed'; readonly kind: FailureKind; readonly message: string }

/** The words shown beside the progress bar for each stage. */
export const STAGE_TEXT: Readonly<Record<RemoveStage, string>> = {
	reading: 'Reading the picture',
	decoding: 'Decoding the picture',
	'loading-model': 'Loading the model',
	running: 'Finding the subject',
	cutting: 'Cutting out the background',
}
