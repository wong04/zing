import type { ToolLoaders } from '../registry'

export const webTools: ToolLoaders = {
	'section:Diagnose': () => import('./DiagnoseTool').then((m) => ({ default: m.DiagnoseTool })),
}
