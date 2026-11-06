import type { PickedFile } from '@shared/engines/engineJob'
import { zing } from '../../api'
import { base64ToBytes, bytesToBase64, checkSaveSize } from './pdfBytes'

/** The bytes of a file the person picked, dropped or saved earlier. The host refuses anything but a PDF or a picture, and a file over 50 MB. */
export async function readGranted(token: string): Promise<Uint8Array> {
	return base64ToBytes(await zing.readFileBase64(token))
}

/** Puts bytes in Downloads under a numbered name (the host decides it); answers the name it got and a token for it. */
export async function saveBytes(name: string, bytes: Uint8Array): Promise<PickedFile> {
	checkSaveSize(bytes)
	return zing.saveFileBase64(name, bytesToBase64(bytes))
}
