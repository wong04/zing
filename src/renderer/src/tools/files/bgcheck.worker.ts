// Reads the picture and judges its format and size before the model is offered. It never loads onnxruntime.
import './bgGuard'
import { checkImage, SNIFF_BYTES } from '@shared/convert/imageSniff'
import { describe, download, MAX_FILE_BYTES, post, scope } from './bgworkerCommon'
import type { CheckRequest } from './bgremoveProtocol'

async function check(request: CheckRequest): Promise<void> {
	const file = await download(request.fileUrl, 'The picture', MAX_FILE_BYTES, () => undefined)
	checkImage(file.subarray(0, SNIFF_BYTES))
	post({ type: 'checked' })
}

scope.onmessage = (event: MessageEvent<CheckRequest>) => {
	check(event.data).catch((error: unknown) => post(describe(error)))
}
