import { PdfEditError } from '@shared/pdf/pdfCore'
import { checkPictureSize } from '@shared/pdf/pictureHeader'

export interface Picture {
	readonly format: 'png' | 'jpg'
	readonly bytes: Uint8Array
}

const PNG = [0x89, 0x50, 0x4e, 0x47]

/** What the first bytes say a picture is; PDF pages can hold only PNG and JPEG as they are. */
export function sniffPicture(bytes: Uint8Array): 'png' | 'jpg' | 'other' {
	if (PNG.every((b, i) => bytes[i] === b)) return 'png'
	if (bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return 'jpg'
	return 'other'
}

export async function canvasToPng(canvas: HTMLCanvasElement, name: string): Promise<Uint8Array> {
	const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/png'))
	if (blob === null) throw new PdfEditError(`${name} is too large to turn into a PNG picture.`)
	return new Uint8Array(await blob.arrayBuffer())
}

/**
 * A PNG or JPEG for a PDF page. Those two are used as they are; GIF (first frame), WebP and BMP are decoded by the browser
 * and re-saved as PNG, with transparency kept.
 */
export async function toPicture(name: string, bytes: Uint8Array): Promise<Picture> {
	// The size is read from the header first: a small file can describe a canvas of billions of pixels.
	checkPictureSize(name, bytes)
	const kind = sniffPicture(bytes)
	if (kind !== 'other') return { format: kind, bytes }
	let bitmap: ImageBitmap
	try {
		bitmap = await createImageBitmap(new Blob([bytes as BlobPart]))
	} catch {
		throw new PdfEditError(`${name} isn't a picture Zing can read.`)
	}
	const canvas = document.createElement('canvas')
	canvas.width = bitmap.width
	canvas.height = bitmap.height
	canvas.getContext('2d')!.drawImage(bitmap, 0, 0)
	bitmap.close()
	try {
		return { format: 'png', bytes: await canvasToPng(canvas, name) }
	} finally {
		canvas.width = 0
		canvas.height = 0
	}
}
