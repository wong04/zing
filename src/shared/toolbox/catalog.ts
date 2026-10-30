// Port of Krackn App/Navigation.swift (`ToolboxGroup`).
//
// How the Toolbox groups its contents: by the job you're doing, six groups of
// four to seven, so each is one glance and the whole set fits on a screen.
// Engine setup isn't here: it's configuration, so it lives in Settings.
import { panelId, panelTitle, section, Section, tool, UtilityTool, type PanelContent } from './panelContent'
import { keywords, type Entry } from './toolboxSearch'

export const ToolboxGroup = {
	files: 'Files & Media',
	web: 'Web & Network',
	security: 'Security',
	developer: 'Developer',
	math: 'Math & Science',
	textDesign: 'Text & Design',
} as const
export type ToolboxGroup = (typeof ToolboxGroup)[keyof typeof ToolboxGroup]

/** Every group, in display order. */
export const toolboxGroups: readonly ToolboxGroup[] = Object.values(ToolboxGroup)

const groupItems: Readonly<Record<ToolboxGroup, readonly PanelContent[]>> = {
	[ToolboxGroup.files]: [
		section(Section.viewer),
		section(Section.convert),
		section(Section.download),
		tool(UtilityTool.bgRemove),
	],
	[ToolboxGroup.web]: [
		section(Section.unblock),
		section(Section.diagnose),
		section(Section.network),
		tool(UtilityTool.linkCleaner),
	],
	[ToolboxGroup.security]: [
		section(Section.security),
		tool(UtilityTool.password),
		tool(UtilityTool.hash),
		tool(UtilityTool.hmac),
		tool(UtilityTool.jwt),
	],
	[ToolboxGroup.developer]: [
		tool(UtilityTool.json),
		tool(UtilityTool.base64),
		tool(UtilityTool.urlCode),
		tool(UtilityTool.regex),
		tool(UtilityTool.uuid),
		tool(UtilityTool.timestamp),
		tool(UtilityTool.cron),
	],
	[ToolboxGroup.math]: [
		tool(UtilityTool.calculator),
		tool(UtilityTool.graphing),
		tool(UtilityTool.units),
		tool(UtilityTool.numberBase),
	],
	[ToolboxGroup.textDesign]: [
		tool(UtilityTool.caseConvert),
		tool(UtilityTool.textStats),
		tool(UtilityTool.color),
		tool(UtilityTool.qr),
	],
}

export function itemsIn(group: ToolboxGroup): readonly PanelContent[] {
	return groupItems[group]
}

/** Everything the Toolbox offers, in display order. */
export function allItems(): PanelContent[] {
	return toolboxGroups.flatMap(itemsIn)
}

export function groupOf(item: PanelContent): ToolboxGroup | null {
	const id = panelId(item)
	return toolboxGroups.find((group) => itemsIn(group).some((candidate) => panelId(candidate) === id)) ?? null
}

/** Whether the recents list may record this item: only things the Toolbox actually offers. */
export function isInToolbox(item: PanelContent): boolean {
	return groupOf(item) !== null
}

/** The catalog as search entries. */
export function searchEntries(): Entry[] {
	return toolboxGroups.flatMap((group) =>
		itemsIn(group).map((item) => {
			const id = panelId(item)
			return { id, title: panelTitle(item), group, keywords: keywords[id] ?? [] }
		}),
	)
}
