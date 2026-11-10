import { readFileSync } from 'node:fs'
import { join } from 'node:path'

// Formats the third-party notices Zing's About > Credits shows. Pure: gen-notices.ts collects the packages.
export interface NoticePackage {
	readonly name: string
	readonly version: string
	readonly license: string
	readonly licenseText: string
	readonly source: 'npm' | 'nuget'
}

interface FixedSection {
	readonly title: string
	readonly lines: readonly string[]
	/** A file in scripts/licenses whose text follows the lines. */
	readonly textFile?: string
}

// From the repo root, so it resolves the same under vitest (scripts/) and in the bundle gen-notices.ts runs from (out/).
const LICENSES_DIR = join(import.meta.dirname, '..', 'scripts', 'licenses')

/** Where the licence text of a package that ships none is committed: `/` is not valid in a file name. */
export function overrideFile(packageName: string): string {
	return join(LICENSES_DIR, `${packageName.replace('/', '__')}.txt`)
}

/** Bundled or relied on, but not a package a lister finds. */
const FIXED_SECTIONS: readonly FixedSection[] = [
	{
		title: 'Public Suffix List',
		lines: [
			'Bundled as host/Zing.Core/Defaults/public_suffix_list.dat, from https://publicsuffix.org/.',
			'License: Mozilla Public License 2.0. The file carries its notice, and its source is the file itself.',
			'Licence text: https://mozilla.org/MPL/2.0/',
		],
	},
	{
		title: 'Inter',
		lines: [
			'The typeface the app is set in, bundled as Inter.ttf. Copyright The Inter Project Authors, https://github.com/rsms/inter',
			'License: SIL Open Font License 1.1.',
			'',
		],
		textFile: 'inter.txt',
	},
	{
		title: 'Microsoft Edge WebView2 Runtime',
		lines: [
			'Zing shows web pages with the WebView2 Runtime installed on this PC. It is not part of Zing and is licensed by Microsoft.',
			'More information: https://developer.microsoft.com/microsoft-edge/webview2/',
		],
	},
	{
		title: 'Windows App SDK',
		lines: [
			'Zing uses the Windows App SDK, shipped with the app and licensed by Microsoft.',
			'Licence terms: https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE',
		],
	},
]

const RULE = '-'.repeat(72)

function ordered(packages: readonly NoticePackage[]): NoticePackage[] {
	const unique = new Map<string, NoticePackage>()
	for (const entry of packages) {
		const key = `${entry.name}@${entry.version}`
		if (!unique.has(key)) unique.set(key, entry)
	}
	return [...unique.values()].sort((a, b) => a.name.localeCompare(b.name, 'en') || a.version.localeCompare(b.version, 'en', { numeric: true }))
}

function section(entry: NoticePackage): string {
	return [RULE, `${entry.name} ${entry.version}`, `License: ${entry.license} (${entry.source})`, '', entry.licenseText.trim(), ''].join('\n')
}

function fixed(entry: FixedSection): string {
	const text = entry.textFile ? [readFileSync(join(LICENSES_DIR, entry.textFile), 'utf8').trim()] : []
	return [RULE, entry.title, ...entry.lines, ...text, ''].join('\n')
}

export function formatNotices(packages: readonly NoticePackage[]): string {
	const unlicensed = packages.filter((entry) => !entry.licenseText.trim()).map((entry) => `${entry.name}@${entry.version}`)
	if (unlicensed.length > 0) throw new Error(`No licence found for ${unlicensed.join(', ')}`)
	const header = ['Third-party notices', '', 'Zing is released under the MIT License. It includes the following third-party software.', '']
	return [...header, ...ordered(packages).map(section), ...FIXED_SECTIONS.map(fixed)].join('\n')
}
