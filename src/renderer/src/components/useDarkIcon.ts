import { useEffect, useState } from 'react'
import { isDarkIcon } from '@shared/theme/darkIcon'

const SAMPLE = 16
const verdicts = new Map<string, boolean>()

export class IconDecodeError extends Error {
	override name = 'IconDecodeError'
}

function sample(image: HTMLImageElement): Uint8ClampedArray {
	const canvas = document.createElement('canvas')
	canvas.width = SAMPLE
	canvas.height = SAMPLE
	const context = canvas.getContext('2d', { willReadFrequently: true })
	if (!context) throw new IconDecodeError('no 2d canvas to read a site icon with')
	context.drawImage(image, 0, 0, SAMPLE, SAMPLE)
	return context.getImageData(0, 0, SAMPLE, SAMPLE).data
}

/** Whether a site icon (a data URL) is so dark it needs a light backing in dark mode; false until it has been read. */
export function useDarkIcon(src: string | undefined): boolean {
	const [read, setRead] = useState<{ src: string; dark: boolean } | undefined>()
	useEffect(() => {
		if (!src || verdicts.has(src)) return
		let live = true
		const image = new Image()
		image.onload = () => {
			const dark = isDarkIcon(sample(image))
			verdicts.set(src, dark)
			if (live) setRead({ src, dark })
		}
		image.onerror = () => {
			throw new IconDecodeError('a site icon could not be decoded')
		}
		image.src = src
		return () => {
			live = false
		}
	}, [src])
	if (!src) return false
	return verdicts.get(src) ?? (read?.src === src && read.dark)
}
