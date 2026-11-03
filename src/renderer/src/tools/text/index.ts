import type { ToolLoaders } from '../registry'

export const textTools: ToolLoaders = {
	'tool:Text Case': () => import('./TextCase'),
	'tool:Text Stats': () => import('./TextStats'),
	'tool:Color Converter': () => import('./ColorConverter'),
	'tool:QR Code': () => import('./QrCode'),
	'tool:Passwords': () => import('./Passwords'),
	'tool:Link Cleaner': () => import('./LinkCleaner'),
}
