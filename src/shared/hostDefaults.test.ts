// The C# host reads TypeScript-defined defaults from committed JSON. This
// fails when a setting, command, bridge channel or the library shape changes
// without regenerating them (`npm run gen:host-defaults`).
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { hostChannels } from '../../scripts/host-channels'
import { SETTING_RULES, hostDefaults, ruleAccepts } from '../../scripts/host-defaults'
import { SETTING_KEYS, acceptsValue } from './settings/schema'

describe('host defaults are current', () => {
	for (const [name, value] of Object.entries({ ...hostDefaults(), 'channels.json': hostChannels() })) {
		it(name, () => {
			const committed = JSON.parse(readFileSync(join(import.meta.dirname, '..', '..', 'host', 'Zing.Core', 'Defaults', name), 'utf8'))
			expect(committed).toEqual(JSON.parse(JSON.stringify(value)))
		})
	}
})

describe('bridge channel list', () => {
	const { invoke, events } = hostChannels()
	it('counts optional arguments as a range', () => {
		expect(invoke.find((e) => e.channel === 'shell:open-settings')).toEqual({ channel: 'shell:open-settings', minArgs: 0, maxArgs: 1 })
		expect(invoke.find((e) => e.channel === 'tabs:move-within')).toEqual({ channel: 'tabs:move-within', args: 3 })
		expect(invoke.find((e) => e.channel === 'tabs:get')).toEqual({ channel: 'tabs:get', args: 0 })
	})
	it('lists events', () => {
		expect(events).toContain('tabs:state')
		expect(events).toContain('chrome:focus-address')
	})
})

describe('host setting rules match the schema validators', () => {
	const shortcut = { title: 't', url: 'u' }
	const sleep = { enabled: true, afterMinutes: 30, sleepPinned: false }
	const probes: unknown[] = [
		true, false, null, 0, 1, -1, 0.24, 0.25, 9, 10, 100, 101, 3.5, 3.0, 22, 23, 24, 58, 59, 60, 8760, 8760.5, 10080, 10081,
		'', 'x', 'top', 'bogus', [], ['a'], [1], {}, [shortcut], [{ title: 't' }], [{ ...shortcut, extra: 1 }], [[]],
		sleep, { ...sleep, afterMinutes: 0 }, { ...sleep, afterMinutes: 1 }, { ...sleep, afterMinutes: 10080 }, { ...sleep, afterMinutes: 10081 },
		{ ...sleep, extra: 1 }, { enabled: true, afterMinutes: 30 }, { ...sleep, enabled: 'yes' },
		...Object.values(SETTING_RULES).flatMap((rule) => (rule.type === 'enum' ? rule.values : [])),
	]
	it('covers exactly the schema keys', () => {
		expect(Object.keys(SETTING_RULES).sort()).toEqual([...SETTING_KEYS].sort())
	})
	for (const key of SETTING_KEYS) {
		it(key, () => {
			for (const probe of probes) {
				expect(ruleAccepts(SETTING_RULES[key], probe), `${key} ${JSON.stringify(probe)}`).toBe(acceptsValue(key, probe))
			}
		})
	}
})
