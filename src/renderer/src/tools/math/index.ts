import type { ToolLoaders } from '../registry'

export const mathTools: ToolLoaders = {
	'tool:Scientific Calculator': () => import('./CalculatorTool').then((m) => ({ default: m.CalculatorTool })),
	'tool:Unit Converter': () => import('./UnitsTool').then((m) => ({ default: m.UnitsTool })),
	'tool:Number Bases': () => import('./NumberBaseTool').then((m) => ({ default: m.NumberBaseTool })),
	'tool:Graphing Calculator': () => import('./GraphingTool').then((m) => ({ default: m.GraphingTool })),
}
