import type { Session } from 'electron'
import { describe, expect, it } from 'vitest'
import { SiteDataKeeper, siteDataKeeperFor } from './privacy'

const session = {} as Session

describe('site data purging', () => {
	it('purges for the in-memory profile', () => expect(siteDataKeeperFor(false, session)).toBeInstanceOf(SiteDataKeeper))
	it('never purges a persistent profile, which exists to keep sign-ins', () => expect(siteDataKeeperFor(true, session)).toBeUndefined())
})
