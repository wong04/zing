// The C# host reads TypeScript-defined defaults from committed JSON. This
// fails when a setting, command or the library shape changes without
// regenerating them (`npm run gen:host-defaults`).
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { hostDefaults } from '../../scripts/host-defaults'

describe('host defaults are current', () => {
	for (const [name, value] of Object.entries(hostDefaults())) {
		it(name, () => {
			const committed = JSON.parse(readFileSync(join(import.meta.dirname, '..', '..', 'host', 'Zing.Core', 'Defaults', name), 'utf8'))
			expect(committed).toEqual(JSON.parse(JSON.stringify(value)))
		})
	}
})
