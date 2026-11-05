import type { ToolLoaders } from '../registry'

export const filesTools: ToolLoaders = {
	'tool:Engines': () => import('./EnginesTool').then((m) => ({ default: m.EnginesTool })),
	'section:Download': () => import('./DownloadTool').then((m) => ({ default: m.DownloadTool })),
}
