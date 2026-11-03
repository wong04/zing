import type { ToolLoaders } from '../registry'

export const linksTools: ToolLoaders = {
	'section:Links': () => import('./LinksTool').then((m) => ({ default: m.LinksTool })),
}
