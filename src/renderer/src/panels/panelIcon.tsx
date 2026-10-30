import { panelContentFromId, Section, UtilityTool } from '@shared/toolbox/panelContent'
import {
	ArrowsIcon,
	CalculatorIcon,
	CodeIcon,
	DownloadIcon,
	FileGlyph,
	GlobeIcon,
	KeyIcon,
	LinkIcon,
	PaletteIcon,
	ShieldIcon,
	TextIcon,
	ToolIcon,
	WaveIcon,
} from '../components/icons'

const SECTION_ICONS: Readonly<Partial<Record<string, (size: number) => React.JSX.Element>>> = {
	[Section.viewer]: (s) => <FileGlyph size={s} />,
	[Section.convert]: (s) => <ArrowsIcon size={s} />,
	[Section.download]: (s) => <DownloadIcon size={s} />,
	[Section.unblock]: (s) => <LinkIcon size={s} />,
	[Section.diagnose]: (s) => <WaveIcon size={s} />,
	[Section.network]: (s) => <GlobeIcon size={s} />,
	[Section.security]: (s) => <ShieldIcon size={s} />,
}

const TOOL_ICONS: Readonly<Partial<Record<string, (size: number) => React.JSX.Element>>> = {
	[UtilityTool.json]: (s) => <CodeIcon size={s} />,
	[UtilityTool.regex]: (s) => <CodeIcon size={s} />,
	[UtilityTool.calculator]: (s) => <CalculatorIcon size={s} />,
	[UtilityTool.graphing]: (s) => <WaveIcon size={s} />,
	[UtilityTool.color]: (s) => <PaletteIcon size={s} />,
	[UtilityTool.password]: (s) => <KeyIcon size={s} />,
	[UtilityTool.jwt]: (s) => <KeyIcon size={s} />,
	[UtilityTool.caseConvert]: (s) => <TextIcon size={s} />,
	[UtilityTool.textStats]: (s) => <TextIcon size={s} />,
	[UtilityTool.linkCleaner]: (s) => <LinkIcon size={s} />,
}

/** A glyph for a panel id such as "tool:Hashes"; tools without a particular glyph share a wrench. */
export function PanelIcon({ content, size = 15 }: { content: string; size?: number }) {
	const parsed = panelContentFromId(content)
	if (!parsed) return <ToolIcon size={size} />
	const table = parsed.kind === 'section' ? SECTION_ICONS : TOOL_ICONS
	const name = parsed.kind === 'section' ? parsed.section : parsed.tool
	return table[name]?.(size) ?? <ToolIcon size={size} />
}
