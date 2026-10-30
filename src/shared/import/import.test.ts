import { mkdtemp, mkdir, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { emptyLibrary, favourites, isFolder, type BookmarkFolder } from '../library/library'
import { ArcSidebarFormatError, parseArcSidebar } from './arcSidebar'
import { ChromiumBookmarksFormatError, parseChromiumBookmarks } from './chromiumBookmarks'
import { CsvSyntaxError, parseCsv } from './csv'
import { countLinks, importedLink, mergeImport } from './importResult'
import { decodeEntities, NetscapeBookmarksFormatError, parseNetscapeBookmarks } from './netscapeBookmarks'
import { parsePasswordCsv, UnknownPasswordCsvError } from './passwords'
import { findChromiumProfiles, profileNames } from './profiles.node'

describe('csv', () => {
	it('handles quoted commas, quotes and newlines', () => {
		expect(parseCsv('a,b\n"x, y","say ""hi""\nthere"\n')).toEqual([
			['a', 'b'],
			['x, y', 'say "hi"\nthere'],
		])
	})
	it('handles CRLF and a BOM', () => expect(parseCsv('﻿a,b\r\n1,2\r\n')).toEqual([['a', 'b'], ['1', '2']]))
	it('keeps empty fields', () => expect(parseCsv('a,,c')).toEqual([['a', '', 'c']]))
	it('rejects an unterminated quote', () => expect(() => parseCsv('"abc')).toThrow(CsvSyntaxError))
})

describe('password CSV import', () => {
	it('reads Chrome/Edge/Dia exports', () => {
		const csv = 'name,url,username,password,note\ngithub.com,https://github.com/login,me,s3cret,"two, words"\n'
		const result = parsePasswordCsv(csv)
		expect(result.format).toBe('chromium')
		expect(result.logins).toEqual([{ title: 'github.com', url: 'https://github.com/login', username: 'me', password: 's3cret', note: 'two, words' }])
	})
	it('reads Bitwarden exports and skips non-login items', () => {
		const csv = [
			'folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp',
			',,login,Mail,,,0,https://mail.example.com,a@b.c,pw,',
			',,note,Secret note,hello,,0,,,,',
		].join('\n')
		const result = parsePasswordCsv(csv)
		expect(result.format).toBe('bitwarden')
		expect(result.logins).toHaveLength(1)
		expect(result.skipped).toEqual([{ row: 3, reason: 'Bitwarden note item, not a login' }])
	})
	it('reads 1Password exports', () => {
		const result = parsePasswordCsv('Title,Url,Username,Password,OTPAuth,Favorite,Archived,Tags,Notes\nBank,https://bank.example,me,pw,,false,false,,\n')
		expect(result.format).toBe('onepassword')
		expect(result.logins[0]?.title).toBe('Bank')
	})
	it('reads Firefox exports, titling by hostname', () => {
		const result = parsePasswordCsv('"url","username","password","httpRealm","formActionOrigin","guid","timeCreated","timeLastUsed","timePasswordChanged"\n"https://a.example","u","p",,"https://a.example","{x}","1","1","1"\n')
		expect(result.format).toBe('firefox')
		expect(result.logins[0]?.title).toBe('a.example')
	})
	it('reports rows it cannot fill instead of dropping them', () => {
		const result = parsePasswordCsv('name,url,username,password,note\napp,android://com.app,u,p,\nsite,https://s.example,u,,\n')
		expect(result.logins).toHaveLength(0)
		expect(result.skipped.map((s) => s.reason)).toEqual(['Not a web address: android://com.app', 'No password'])
	})
	it('refuses an unknown CSV', () => expect(() => parsePasswordCsv('a,b\n1,2\n')).toThrow(UnknownPasswordCsvError))
})

const CHROME_BOOKMARKS = JSON.stringify({
	checksum: 'x',
	roots: {
		bookmark_bar: {
			type: 'folder',
			name: 'Bookmarks bar',
			children: [
				{ type: 'url', name: 'GitHub', url: 'https://github.com/' },
				{ type: 'folder', name: 'Dev', children: [{ type: 'url', name: 'MDN', url: 'https://developer.mozilla.org/' }] },
				{ type: 'url', name: 'Bookmarklet', url: 'javascript:alert(1)' },
			],
		},
		other: { type: 'folder', name: 'Other bookmarks', children: [{ type: 'url', name: 'News', url: 'https://news.example/' }] },
		synced: { type: 'folder', name: 'Mobile bookmarks', children: [{ type: 'url', name: 'Phone', url: 'https://phone.example/' }] },
	},
	version: 1,
})

describe('Chromium Bookmarks JSON (Chrome, Edge, Brave, Dia)', () => {
	const result = parseChromiumBookmarks(CHROME_BOOKMARKS)
	it('maps the bookmarks bar to the bar, keeping folders', () => {
		expect(result.bar.map((n) => n.title)).toEqual(['GitHub', 'Dev'])
		expect(countLinks(result.bar)).toBe(2)
	})
	it('puts Other bookmarks at the top level and Mobile in a folder', () => {
		expect(result.other.map((n) => n.title)).toEqual(['News', 'Mobile bookmarks'])
	})
	it('skips javascript: bookmarklets out loud', () => expect(result.skipped).toEqual(['Bookmarklet: only http(s) addresses can be bookmarks']))
	it('rejects files that are not Bookmarks', () => expect(() => parseChromiumBookmarks('{}')).toThrow(ChromiumBookmarksFormatError))
})

const NETSCAPE = `<!DOCTYPE NETSCAPE-Bookmark-file-1>
<!-- This is an automatically generated file. -->
<META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
<TITLE>Bookmarks</TITLE>
<H1>Bookmarks</H1>
<DL><p>
    <DT><H3 ADD_DATE="1" PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
    <DL><p>
        <DT><A HREF="https://github.com/" ADD_DATE="1">GitHub</A>
        <DT><H3 ADD_DATE="1">Dev &amp; Docs</H3>
        <DL><p>
            <DT><A HREF="https://developer.mozilla.org/?a=1&amp;b=2">MDN</A>
        </DL><p>
    </DL><p>
    <DT><A HREF="https://news.example/">Caf&#233; news</A>
    <DT><A HREF="javascript:void(0)">Run</A>
    <DT><H3>Empty</H3>
    <DL><p>
    </DL><p>
</DL><p>
`

describe('Netscape bookmarks HTML (every browser, incl. Dia on Mac)', () => {
	const result = parseNetscapeBookmarks(NETSCAPE)
	it('finds the toolbar folder as the bar', () => {
		expect(result.bar.map((n) => n.title)).toEqual(['GitHub', 'Dev & Docs'])
	})
	it('decodes entities in titles and URLs', () => {
		const dev = result.bar[1]
		expect(dev?.kind === 'folder' ? dev.children[0] : undefined).toEqual(importedLink('MDN', 'https://developer.mozilla.org/?a=1&b=2'))
		expect(result.other[0]?.title).toBe('Café news')
	})
	it('keeps empty folders and skips javascript: links', () => {
		expect(result.other.map((n) => n.title)).toEqual(['Café news', 'Empty'])
		expect(result.skipped).toEqual(['Run: only http(s) addresses can be bookmarks'])
	})
	it('decodes hex and named entities', () => expect(decodeEntities('&#x41;&lt;&quot;&unknown;')).toBe('A<"&unknown;'))
	it('rejects unrelated HTML', () => expect(() => parseNetscapeBookmarks('<html><body>hi</body></html>')).toThrow(NetscapeBookmarksFormatError))
})

const ARC = JSON.stringify({
	sidebar: {
		containers: [
			{ global: {} },
			{
				spaces: ['s1', { id: 's1', title: 'Work', containerIDs: ['pinned', 'p1', 'unpinned', 'u1'] }],
				items: [
					'p1', { id: 'p1', title: null, childrenIds: ['t1', 'f1'], data: { itemContainer: {} } },
					't1', { id: 't1', title: null, childrenIds: [], data: { tab: { savedTitle: 'Linear', savedURL: 'https://linear.app/' } } },
					'f1', { id: 'f1', title: 'Docs', childrenIds: ['t2'], data: { list: {} } },
					't2', { id: 't2', title: 'Renamed', childrenIds: [], data: { tab: { savedTitle: 'Notion', savedURL: 'https://notion.so/' } } },
					'top', { id: 'top', title: null, childrenIds: ['t3'], data: { itemContainer: {} } },
					't3', { id: 't3', title: null, childrenIds: [], data: { tab: { savedTitle: 'Gmail', savedURL: 'https://mail.google.com/' } } },
				],
				topAppsContainerIDs: ['default', 'top'],
			},
		],
	},
})

describe('Arc StorableSidebar.json', () => {
	const result = parseArcSidebar(ARC)
	it('turns each Space into a folder of its pinned tabs', () => {
		const work = result.other[0]
		expect(work?.title).toBe('Work')
		expect(work?.kind === 'folder' ? work.children.map((n) => n.title) : []).toEqual(['Linear', 'Docs'])
	})
	it('prefers a renamed title over the saved page title', () => {
		const work = result.other[0]
		const docs = work?.kind === 'folder' ? work.children[1] : undefined
		expect(docs?.kind === 'folder' ? docs.children[0]?.title : '').toBe('Renamed')
	})
	it('maps Arc Favorites to the bar', () => expect(result.bar.map((n) => n.title)).toEqual(['Gmail']))
	it('fails loudly on an unexpected layout', () => expect(() => parseArcSidebar('{"sidebar":{}}')).toThrow(ArcSidebarFormatError))
})

describe('merging an import into the library', () => {
	const result = parseChromiumBookmarks(CHROME_BOOKMARKS)
	const once = mergeImport(emptyLibrary(), result, 'Chrome', 1)
	it('adds bar items to Favourites', () => expect(favourites(once).map((n) => n.title)).toEqual(['GitHub', 'Dev']))
	it('puts the rest in one Imported folder', () => {
		const imported = once.bookmarks.find((n) => n.title === 'Imported from Chrome') as BookmarkFolder
		expect(imported.children.map((n) => n.title)).toEqual(['News', 'Mobile bookmarks'])
	})
	it('a second import does not duplicate bar links or the folder', () => {
		const twice = mergeImport(once, result, 'Chrome', 2)
		expect(favourites(twice).filter((n) => !isFolder(n))).toHaveLength(1)
		expect(twice.bookmarks.filter((n) => n.title === 'Imported from Chrome')).toHaveLength(1)
	})
})

describe('finding Chromium profiles on this PC', () => {
	it('reads display names from Local State', () => {
		const names = profileNames(JSON.stringify({ profile: { info_cache: { Default: { name: 'Personal' }, 'Profile 1': { name: 'Work' } } } }))
		expect(names.get('Profile 1')).toBe('Work')
	})
	it('finds profiles that have a Bookmarks file', async () => {
		const root = await mkdtemp(join(tmpdir(), 'zing-import-'))
		const chrome = join(root, 'Google', 'Chrome', 'User Data')
		await mkdir(join(chrome, 'Default'), { recursive: true })
		await mkdir(join(chrome, 'Profile 1'), { recursive: true })
		await mkdir(join(chrome, 'System Profile'), { recursive: true })
		await mkdir(join(chrome, 'Guest Profile'), { recursive: true })
		await writeFile(join(chrome, 'Guest Profile', 'Bookmarks'), CHROME_BOOKMARKS)
		await writeFile(join(chrome, 'Local State'), JSON.stringify({ profile: { info_cache: { Default: { name: 'Personal' }, 'Profile 1': { name: 'Work' } } } }))
		await writeFile(join(chrome, 'Default', 'Bookmarks'), CHROME_BOOKMARKS)
		await writeFile(join(chrome, 'Profile 1', 'Bookmarks'), CHROME_BOOKMARKS)
		const profiles = await findChromiumProfiles(root)
		expect(profiles.map((p) => `${p.browser}:${p.name}`).sort()).toEqual(['chrome:Personal', 'chrome:Work'])
	})
})
