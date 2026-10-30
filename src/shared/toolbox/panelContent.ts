// Port of Krackn's `Section` (App/KracknApp.swift), `UtilityTool`
// (Features/Utilities/UtilitiesView.swift) and `PanelContent`
// (App/FloatingPanels.swift).
//
// What a floating panel, a Toolbox tile or a recents row is showing: either a
// whole section or a single tool. Identified by a string id
// ("section:Files", "tool:Hashes") so the recents list can be stored as plain
// strings.

/** The app's larger features. Most live in the Toolbox; panels and recents identify them by this. */
export const Section = {
	browser: 'Browser',
	viewer: 'Files',
	convert: 'Convert',
	utilities: 'Toolbox',
	download: 'Download',
	unblock: 'Open a Link',
	diagnose: 'Diagnose',
	network: 'Network & VPN',
	security: 'Security',
	settings: 'Settings',
} as const
export type Section = (typeof Section)[keyof typeof Section]

/** Single tools, titled as the Toolbox shows them. */
export const UtilityTool = {
	tools: 'Engines',
	json: 'JSON / YAML',
	base64: 'Base64',
	urlCode: 'URL Encoding',
	numberBase: 'Number Bases',
	regex: 'Regex Tester',
	hash: 'Hashes',
	hmac: 'HMAC',
	jwt: 'JWT Decoder',
	password: 'Passwords',
	caseConvert: 'Text Case',
	textStats: 'Text Stats',
	linkCleaner: 'Link Cleaner',
	calculator: 'Scientific Calculator',
	graphing: 'Graphing Calculator',
	units: 'Unit Converter',
	timestamp: 'Timestamps',
	cron: 'Cron Parser',
	color: 'Color Converter',
	bgRemove: 'Background Remover',
	qr: 'QR Code',
	uuid: 'UUID',
} as const
export type UtilityTool = (typeof UtilityTool)[keyof typeof UtilityTool]

export type PanelContent =
	| { readonly kind: 'section'; readonly section: Section }
	| { readonly kind: 'tool'; readonly tool: UtilityTool }

export type Size = { readonly width: number; readonly height: number }

const sectionPrefix = 'section:'
const toolPrefix = 'tool:'
const sections: readonly string[] = Object.values(Section)
const tools: readonly string[] = Object.values(UtilityTool)

export function section(value: Section): PanelContent {
	return { kind: 'section', section: value }
}

export function tool(value: UtilityTool): PanelContent {
	return { kind: 'tool', tool: value }
}

export function panelId(content: PanelContent): string {
	if (content.kind === 'section') return sectionPrefix + content.section
	return toolPrefix + content.tool
}

/** Round-trips `panelId`. Null for an id that names nothing (a tool removed since it was stored, say). */
export function panelContentFromId(id: string): PanelContent | null {
	if (id.startsWith(sectionPrefix)) {
		const name = id.slice(sectionPrefix.length)
		return isSection(name) ? section(name) : null
	}
	if (id.startsWith(toolPrefix)) {
		const name = id.slice(toolPrefix.length)
		return isUtilityTool(name) ? tool(name) : null
	}
	return null
}

export function panelTitle(content: PanelContent): string {
	return content.kind === 'section' ? content.section : content.tool
}

export function samePanel(a: PanelContent, b: PanelContent): boolean {
	return panelId(a) === panelId(b)
}

/**
 * The browser is deliberately not poppable. It owns long-lived web views,
 * and a second live copy of that view hierarchy would fight the main one over
 * which container owns each web view.
 */
export function isTearable(value: Section): boolean {
	return value !== Section.browser && value !== Section.utilities && value !== Section.settings
}

/**
 * Panels are sized to what they hold. A one-size-fits-all box either wastes
 * half its area on the UUID generator or crops the graphing calculator's plot.
 */
export function preferredSize(content: PanelContent): Size {
	if (content.kind === 'section') return sectionSize(content.section)
	return toolSize(content.tool)
}

function sectionSize(value: Section): Size {
	switch (value) {
		case Section.viewer:
		case Section.convert:
			return { width: 520, height: 580 }
		case Section.settings:
		case Section.security:
		case Section.network:
			return { width: 540, height: 620 }
		case Section.utilities:
			return { width: 520, height: 600 }
		default:
			return { width: 480, height: 540 }
	}
}

function toolSize(value: UtilityTool): Size {
	switch (value) {
		// Needs room for the expression list beside the plot.
		case UtilityTool.graphing:
			return { width: 640, height: 560 }
		case UtilityTool.bgRemove:
		case UtilityTool.qr:
			return { width: 500, height: 620 }
		case UtilityTool.regex:
		case UtilityTool.jwt:
		case UtilityTool.json:
		case UtilityTool.textStats:
		case UtilityTool.hash:
		case UtilityTool.hmac:
		case UtilityTool.base64:
			return { width: 520, height: 580 }
		case UtilityTool.calculator:
			return { width: 520, height: 540 }
		default:
			return { width: 460, height: 500 }
	}
}

function isSection(name: string): name is Section {
	return sections.includes(name)
}

function isUtilityTool(name: string): name is UtilityTool {
	return tools.includes(name)
}
