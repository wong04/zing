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
			'Licence text: https://github.com/rsms/inter/blob/master/LICENSE.txt',
		],
	},
	{
		title: 'Microsoft Edge WebView2 Runtime',
		lines: [
			'Zing shows web pages with the WebView2 Runtime installed on this PC. It is not part of Zing and is licensed by Microsoft.',
			'Licence terms: https://developer.microsoft.com/microsoft-edge/webview2/',
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
	return [RULE, entry.title, ...entry.lines, ''].join('\n')
}

export function formatNotices(packages: readonly NoticePackage[]): string {
	const unlicensed = packages.filter((entry) => !entry.licenseText.trim()).map((entry) => `${entry.name}@${entry.version}`)
	if (unlicensed.length > 0) throw new Error(`No licence found for ${unlicensed.join(', ')}`)
	const header = ['Third-party notices', '', 'Zing is released under the MIT License. It includes the following third-party software.', '']
	return [...header, ...ordered(packages).map(section), ...FIXED_SECTIONS.map(fixed)].join('\n')
}
