import { describe, expect, it } from 'vitest'
import { Stall, StallError } from './stall'

describe('Stall', () => {
	it('is quiet while bytes keep coming, however long it goes on', () => {
		let now = 0
		const stall = new Stall(1000, () => now)
		for (let i = 0; i < 100; i++) {
			now += 900
			stall.check()
			stall.progress()
		}
	})
	it('raises a plain error once nothing has come for the limit', () => {
		let now = 0
		const stall = new Stall(1000, () => now)
		now = 999
		stall.check()
		now = 1001
		expect(() => stall.check()).toThrow(StallError)
		expect(() => stall.check()).toThrow(/stopped/)
	})
	it('counts from the last progress', () => {
		let now = 0
		const stall = new Stall(1000, () => now)
		now = 900
		stall.progress()
		now = 1800
		stall.check()
		now = 1901
		expect(() => stall.check()).toThrow(StallError)
	})
})
