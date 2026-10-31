// Port of Krackn `Tests/taborg` "registrable domain". Cases are shared with the C# port.
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { registrableDomain } from './registrableDomain'

const { cases } = JSON.parse(readFileSync(new URL('./registrableDomain.vectors.json', import.meta.url), 'utf8')) as {
	cases: { host: string; want: string }[]
}

describe('registrable domain', () => {
	it.each(cases.map((c) => [c.host, c.want] as const))('%s -> %s', (host, want) => {
		expect(registrableDomain(host)).toBe(want)
	})
})
