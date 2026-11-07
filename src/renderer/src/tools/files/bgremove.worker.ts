// Runs in a dedicated worker, so decoding, the model and the cut-out never block the interface. Everything is fetched
// from this app's own address or the host's file server (ViewerFiles.cs): the WebAssembly from the bundle, the picture and the model from the server.
import './bgGuard'
import * as ort from 'onnxruntime-web/wasm'
import wasmUrl from 'onnxruntime-web/ort-wasm-simd-threaded.wasm?url'
import { applyMask, normaliseMask, resizeMask } from '@shared/convert/alphaMask'
import { MODEL_SIZE, toModelInput, type Normalisation } from '@shared/convert/bgPrep'
import { checkImage, MAX_PIXELS, SNIFF_BYTES } from '@shared/convert/imageSniff'
import { describe, download, MAX_FILE_BYTES, post, progress, scope, WorkerFailure } from './bgworkerCommon'
import type { RunRequest } from './bgremoveProtocol'

/** Checked against the real model: see bgremove.real.test.ts. */
const NORMALISATION: Normalisation = { mean: [0.5, 0.5, 0.5], std: [1, 1, 1], divideByMax: false }

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

/** PUTs the PNG to the host, which checks it, names and numbers the file and writes it off the interface thread. */
async function save(url: string, png: Blob): Promise<{ name: string; token: string }> {
	const response = await fetch(url, { method: 'PUT', body: png })
	if (!response.ok) throw new WorkerFailure('unavailable', `The picture could not be saved: ${(await response.text()) || `the host answered ${response.status}`}.`)
	const saved = (await response.json()) as { name: string; token?: string }
	if (typeof saved.token !== 'string') throw new WorkerFailure('unavailable', 'The picture was saved, but the host gave no way to refer to it.')
	return { name: saved.name, token: saved.token }
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
		const png = await canvas.convertToBlob({ type: 'image/png' })
		progress('saving')
		const { name, token } = await save(request.saveUrl, png)
		post({ type: 'done', name, token, width: picture.width, height: picture.height, seconds: (performance.now() - started) / 1000 })
	} finally {
		await session.release()
	}
}

scope.onmessage = (event: MessageEvent<RunRequest>) => {
	run(event.data).catch((error: unknown) => post(describe(error)))
}
