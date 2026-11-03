import { describe, expect, it } from 'vitest'
import { tool, UtilityTool } from '@shared/toolbox/panelContent'
import { buildRegistry, DuplicateToolError, toolComponent } from './registry'

const body = () => Promise.resolve({ default: () => null })

describe('the tool registry', () => {
	it('has no body for a tool nobody has registered yet', () => {
		expect(toolComponent(tool(UtilityTool.hash))).toBeNull()
	})
	it('wraps each registered tool by its panel id', () => {
		const registry = buildRegistry([{ 'tool:Hashes': body }, { 'tool:UUID': body }])
		expect([...registry.keys()]).toEqual(['tool:Hashes', 'tool:UUID'])
	})
	it('refuses two groups claiming the same tool', () => {
		expect(() => buildRegistry([{ 'tool:Hashes': body }, { 'tool:Hashes': body }])).toThrow(DuplicateToolError)
	})
})
