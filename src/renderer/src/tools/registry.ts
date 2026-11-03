import { lazy, type ComponentType, type LazyExoticComponent } from 'react'
import { panelId, type PanelContent } from '@shared/toolbox/panelContent'
import { devTools } from './dev'
import { mathTools } from './math'
import { textTools } from './text'

/** Loads one tool's panel body on first use, so a tool that is never opened costs nothing. */
export type ToolLoader = () => Promise<{ default: ComponentType }>

/** Keyed by `panelId` ("tool:Hashes"). Each group of tools keeps its own map, so adding a tool touches one file. */
export type ToolLoaders = Readonly<Record<string, ToolLoader>>

export class DuplicateToolError extends Error {
	override name = 'DuplicateToolError'
	constructor(id: string) {
		super(`Two tool groups both register ${id}`)
	}
}

export function buildRegistry(groups: readonly ToolLoaders[]): ReadonlyMap<string, LazyExoticComponent<ComponentType>> {
	const registry = new Map<string, LazyExoticComponent<ComponentType>>()
	for (const group of groups) {
		for (const [id, loader] of Object.entries(group)) {
			if (registry.has(id)) throw new DuplicateToolError(id)
			registry.set(id, lazy(loader))
		}
	}
	return registry
}

const registry = buildRegistry([mathTools, devTools, textTools])

/** The body of a tool's panel, or null while the tool has none (the panel then shows its empty state). */
export function toolComponent(content: PanelContent): LazyExoticComponent<ComponentType> | null {
	return registry.get(panelId(content)) ?? null
}
