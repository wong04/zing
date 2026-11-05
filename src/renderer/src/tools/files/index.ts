import type { ToolLoaders } from '../registry'

export const filesTools: ToolLoaders = {
	'tool:Engines': () => import('./EnginesTool').then((m) => ({ default: m.EnginesTool })),
	'section:Files': () => import('./ViewerTool').then((m) => ({ default: m.ViewerTool })),
}
