// Runs in a dedicated worker, so decoding, the model and the cut-out never block the interface. Everything is fetched
// from this app's own address: the picture and the model from the host (see LocalFiles.cs), the WebAssembly from the bundle.
import * as ort from 'onnxruntime-web/wasm'
import wasmUrl from 'onnxruntime-web/ort-wasm-simd-threaded.wasm?url'
import { applyMask, normaliseMask, resizeMask } from '@shared/convert/alphaMask'
import { MODEL_SIZE, toModelInput, type Normalisation } from '@shared/convert/bgPrep'
import { checkImage, CorruptImageError, ImageTooLargeError, MAX_PIXELS, SNIFF_BYTES, UnsupportedImageError } from '@shared/convert/imageSniff'
import type { CheckRequest, FailureKind, RemoveMessage, RemoveRequest, RemoveStage, RunRequest } from './bgremoveProtocol'

/** Checked against the real model: see bgremove.real.test.ts. */
const NORMALISATION: Normalisation = { mean: [0.5, 0.5, 0.5], std: [1, 1, 1], divideByMax: false }

/** A compressed picture of this size is already past anything a camera makes; the pixel limit is the real guard. */
const MAX_FILE_BYTES = 120 * 1024 * 1024

class WorkerFailure extends Error {
	override name = 'WorkerFailure'
	constructor(
		readonly kind: FailureKind,
		message: string,
	) {
		super(message)
	}
}

// The page's DOM typings are in use, and they give `self` the window's postMessage.
const scope = self as unknown as { postMessage(message: RemoveMessage, transfer: Transferable[]): void; onmessage: ((event: MessageEvent<RemoveRequest>) => void) | null }

function post(message: RemoveMessage, transfer: Transferable[] = []): void {
	scope.postMessage(message, transfer)
}

const progress = (stage: RemoveStage, fraction?: number): void => post({ type: 'progress', stage, fraction })

/** Reads a whole response into one array, telling how far it has got. */
async function download(url: string, what: string, limit: number, report: (fraction: number) => void): Promise<Uint8Array> {
	const response = await fetch(url, { cache: 'no-store' })
	if (!response.ok) throw new WorkerFailure('unavailable', `${what} could not be read from Zing (${response.status}).`)
	const total = Number(response.headers.get('Content-Length'))
	if (!Number.isFinite(total) || total <= 0) throw new WorkerFailure('unavailable', `${what} came without a size, so Zing will not read it.`)
	if (total > limit) throw new WorkerFailure('too-large', `${what} is ${Math.round(total / 1048576)} MB, more than Zing will read.`)
	if (response.body === null) throw new WorkerFailure('unavailable', `${what} came empty.`)
	const bytes = new Uint8Array(total)
	let filled = 0
	const reader = response.body.getReader()
	for (;;) {
		const { done, value } = await reader.read()
		if (done) break
		if (filled + value.length > total) throw new WorkerFailure('unavailable', `${what} was longer than it said.`)
		bytes.set(value, filled)
		filled += value.length
		report(filled / total)
	}
	if (filled !== total) throw new WorkerFailure('unavailable', `${what} stopped early.`)
	return bytes
}

async function decode(bytes: Uint8Array, format: string): Promise<{ bitmap: ImageBitmap; rgba: Uint8ClampedArray; width: number; height: number }> {
	let bitmap: ImageBitmap
	try {
		bitmap = await createImageBitmap(new Blob([bytes as BlobPart], { type: `image/${format}` }))
	} catch {
		throw new WorkerFailure('corrupt', 'This file looks damaged or cut short, so it could not be decoded as a picture.')
	}
	// A rotation recorded in the file can swap the sides after the header was read.
	if (bitmap.width * bitmap.height > MAX_PIXELS) {
		bitmap.close()
		throw new WorkerFailure('too-large', 'This picture is larger than the background remover takes.')
	}
	const canvas = new OffscreenCanvas(bitmap.width, bitmap.height)
	const context = canvas.getContext('2d', { willReadFrequently: true })!
	context.drawImage(bitmap, 0, 0)
	const { data, width, height } = context.getImageData(0, 0, bitmap.width, bitmap.height)
	return { bitmap, rgba: data, width, height }
}

function modelPixels(bitmap: ImageBitmap): Uint8ClampedArray {
	const canvas = new OffscreenCanvas(MODEL_SIZE, MODEL_SIZE)
	const context = canvas.getContext('2d', { willReadFrequently: true })!
	context.imageSmoothingQuality = 'high'
	context.drawImage(bitmap, 0, 0, MODEL_SIZE, MODEL_SIZE)
	return context.getImageData(0, 0, MODEL_SIZE, MODEL_SIZE).data
}

async function createSession(modelBytes: Uint8Array): Promise<ort.InferenceSession> {
	// Everything from this app's own bundle; nothing from a CDN. One thread, because the page is not cross-origin isolated.
	ort.env.wasm.wasmPaths = { wasm: new URL(wasmUrl, self.location.href).href }
	ort.env.wasm.numThreads = 1
	ort.env.wasm.proxy = false
	try {
		return await ort.InferenceSession.create(modelBytes, { executionProviders: ['wasm'], graphOptimizationLevel: 'all' })
	} catch (error) {
		throw new WorkerFailure('model', `The background model could not be loaded (${error instanceof Error ? error.message : String(error)}). Remove it in Engines and download it again.`)
	}
}

async function check(request: CheckRequest): Promise<void> {
	const file = await download(request.fileUrl, 'The picture', MAX_FILE_BYTES, () => undefined)
	checkImage(file.subarray(0, SNIFF_BYTES))
	post({ type: 'checked' })
}

async function run(request: RunRequest): Promise<void> {
	const started = performance.now()
	progress('reading')
	const file = await download(request.fileUrl, 'The picture', MAX_FILE_BYTES, (fraction) => progress('reading', fraction))
	const info = checkImage(file.subarray(0, SNIFF_BYTES))
	progress('decoding')
	const picture = await decode(file, info.format)

	progress('loading-model', 0)
	const session = await createSession(await download(request.modelUrl, 'The background model', 512 * 1024 * 1024, (fraction) => progress('loading-model', fraction)))
	try {
		progress('running')
		const input = new ort.Tensor('float32', toModelInput(modelPixels(picture.bitmap), MODEL_SIZE, MODEL_SIZE, NORMALISATION), [1, 3, MODEL_SIZE, MODEL_SIZE])
		picture.bitmap.close()
		const inputName = session.inputNames[0]!
		const outputName = session.outputNames[0]!
		const output = (await session.run({ [inputName]: input }))[outputName]!
		const expected = [1, 1, MODEL_SIZE, MODEL_SIZE]
		if (output.dims.length !== 4 || output.dims.some((d, i) => d !== expected[i])) {
			throw new WorkerFailure('model', `The model gave a ${output.dims.join(' by ')} answer, not ${expected.join(' by ')}, so it is not the model Zing expects.`)
		}
		progress('cutting')
		const mask = resizeMask(normaliseMask(output.data as Float32Array), MODEL_SIZE, MODEL_SIZE, picture.width, picture.height)
		const cut = applyMask(picture.rgba, mask, picture.width, picture.height)
		const canvas = new OffscreenCanvas(picture.width, picture.height)
		canvas.getContext('2d')!.putImageData(new ImageData(cut, picture.width, picture.height), 0, 0)
		const png = await (await canvas.convertToBlob({ type: 'image/png' })).arrayBuffer()
		post({ type: 'done', png, width: picture.width, height: picture.height, seconds: (performance.now() - started) / 1000 }, [png])
	} finally {
		await session.release()
	}
}

function describe(error: unknown): RemoveMessage & { type: 'failed' } {
	if (error instanceof WorkerFailure) return { type: 'failed', kind: error.kind, message: error.message }
	if (error instanceof UnsupportedImageError) return { type: 'failed', kind: 'unsupported', message: error.message }
	if (error instanceof ImageTooLargeError) return { type: 'failed', kind: 'too-large', message: error.message }
	if (error instanceof CorruptImageError) return { type: 'failed', kind: 'corrupt', message: error.message }
	if (error instanceof RangeError) return { type: 'failed', kind: 'model', message: `The model answer was not usable: ${error.message}.` }
	return { type: 'failed', kind: 'unavailable', message: error instanceof Error ? error.message : String(error) }
}

scope.onmessage = (event: MessageEvent<RemoveRequest>) => {
	const request = event.data
	const work = request.type === 'check' ? check(request) : run(request)
	work.catch((error: unknown) => post(describe(error)))
}
