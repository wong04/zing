import type { ToolLoaders } from '../registry'

export const webTools: ToolLoaders = {
	'section:Open a Link': () => import('./OpenLinkTool').then((m) => ({ default: m.OpenLinkTool })),
	'section:Diagnose': () => import('./DiagnoseTool').then((m) => ({ default: m.DiagnoseTool })),
	'section:Network & VPN': () => import('./NetworkTool').then((m) => ({ default: m.NetworkTool })),
}
