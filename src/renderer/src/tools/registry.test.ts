import { describe, expect, it } from 'vitest'
import { itemsIn, toolboxGroups } from '@shared/toolbox/catalog'
import { panelContentFromId, panelId, tool, UtilityTool } from '@shared/toolbox/panelContent'
import { buildRegistry, DuplicateToolError, registeredToolIds, toolComponent } from './registry'

/** Catalog items whose tool arrives in a later phase; their panel shows the empty state. */
const laterPhase: readonly string[] = [
	'section:Convert',
	'section:Diagnose',
	'section:Files',
	'section:Network & VPN',
	'section:Open a Link',
	'tool:Background Remover',
]

const body = () => Promise.resolve({ default: () => null })

describe('the tool registry', () => {
	it('has no body for a tool nobody has registered yet', () => {
		expect(toolComponent(tool(UtilityTool.bgRemove))).toBeNull()
	})
	it('wraps each registered tool by its panel id', () => {
		const registry = buildRegistry([{ 'tool:Hashes': body }, { 'tool:UUID': body }])
		expect([...registry.keys()]).toEqual(['tool:Hashes', 'tool:UUID'])
	})
	it('refuses two groups claiming the same tool', () => {
		expect(() => buildRegistry([{ 'tool:Hashes': body }, { 'tool:Hashes': body }])).toThrow(DuplicateToolError)
	})
})

describe('the real registry', () => {
	it('registers only ids that name a tool', () => {
		for (const id of registeredToolIds()) {
			const content = panelContentFromId(id)
			expect(content, id).not.toBeNull()
			expect(panelId(content!), id).toBe(id)
		}
	})
	it('has a body for every catalog item except those that wait for a later phase', () => {
		const catalog = toolboxGroups.flatMap((group) => itemsIn(group))
		const bodiless = catalog.filter((content) => toolComponent(content) === null).map(panelId)
		expect(bodiless.sort()).toEqual([...laterPhase].sort())
	})
	it('has a body for Engines, which Settings opens and the Toolbox has no tile for', () => {
		expect(toolComponent(tool(UtilityTool.tools))).not.toBeNull()
		expect(toolboxGroups.flatMap((group) => itemsIn(group)).map(panelId)).not.toContain('tool:Engines')
	})
	it('lists no tool as later-phase once it has a body', () => {
		expect(laterPhase.filter((id) => registeredToolIds().includes(id))).toEqual([])
	})
})
