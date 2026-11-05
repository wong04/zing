import type { ToolLoaders } from '../registry'

export const filesTools: ToolLoaders = {
	'section:Convert': () => import('./ConvertTool').then((m) => ({ default: m.ConvertTool })),
	'tool:Engines': () => import('./EnginesTool').then((m) => ({ default: m.EnginesTool })),
	'section:Download': () => import('./DownloadTool').then((m) => ({ default: m.DownloadTool })),
}
