import { describe, expect, it } from 'vitest'
import { SiteUsage } from './site-usage'

describe('SiteUsage', () => {
	it('purges every origin of a site when its last tab closes', () => {
		const usage = new SiteUsage()
		usage.visit('t1', 'https://mail.example.com/inbox')
		usage.visit('t1', 'https://docs.example.com/')
		const [site, ...rest] = usage.close('t1')
		expect(rest).toEqual([])
		expect(site?.domain).toBe('example.com')
		expect([...(site?.origins ?? [])].sort()).toEqual(['https://docs.example.com', 'https://mail.example.com'])
	})

	it('keeps a site while another tab still uses it', () => {
		const usage = new SiteUsage()
		usage.visit('t1', 'https://example.com/a')
		usage.visit('t2', 'https://www.example.com/b')
		expect(usage.close('t1')).toEqual([])
		expect(usage.close('t2')).toHaveLength(1)
	})

	it('treats a tab that moved on as still having used the earlier site', () => {
		const usage = new SiteUsage()
		usage.visit('t1', 'https://a.test/')
		usage.visit('t1', 'https://b.test/')
		expect(usage.close('t1').map((s) => s.domain).sort()).toEqual(['a.test', 'b.test'])
	})

	it('ignores non-web pages and unknown tabs', () => {
		const usage = new SiteUsage()
		usage.visit('t1', 'about:blank')
		usage.visit('t1', 'not a url')
		expect(usage.close('t1')).toEqual([])
		expect(usage.close('missing')).toEqual([])
	})

	it('does not purge a site twice', () => {
		const usage = new SiteUsage()
		usage.visit('t1', 'http://127.0.0.1:8080/')
		expect(usage.close('t1')).toHaveLength(1)
		expect(usage.close('t1')).toEqual([])
	})
})
