import type { ToolLoaders } from '../registry'

export const textTools: ToolLoaders = {
	'tool:Text Case': () => import('./TextCase').then((m) => ({ default: m.TextCase })),
	'tool:Text Stats': () => import('./TextStats').then((m) => ({ default: m.TextStats })),
	'tool:Color Converter': () => import('./ColorConverter').then((m) => ({ default: m.ColorConverter })),
	'tool:QR Code': () => import('./QrCode').then((m) => ({ default: m.QrCode })),
	'tool:Passwords': () => import('./Passwords').then((m) => ({ default: m.Passwords })),
	'tool:Link Cleaner': () => import('./LinkCleaner').then((m) => ({ default: m.LinkCleaner })),
}
