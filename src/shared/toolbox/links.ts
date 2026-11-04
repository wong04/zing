import { InvalidLinkError, normalizeLink } from '../browser/linkCleaner'

/** One tile on the Links page. Its identity is its `url`. */
export interface ToolLink {
	readonly title: string
	readonly url: string
}

export const DEFAULT_TOOL_LINKS: readonly ToolLink[] = [
	{ title: 'Desmos', url: 'https://www.desmos.com/calculator' },
	{ title: 'Figma', url: 'https://www.figma.com/' },
	{ title: 'Notion', url: 'https://www.notion.so/' },
	{ title: 'ChatGPT', url: 'https://chatgpt.com/' },
	{ title: 'Claude', url: 'https://claude.ai/' },
	{ title: 'Gemini', url: 'https://gemini.google.com/' },
]

export function isToolLinkList(value: unknown): value is readonly ToolLink[] {
	return (
		Array.isArray(value) &&
		value.every((item) => typeof item === 'object' && item !== null && typeof item.title === 'string' && typeof item.url === 'string')
	)
}

/** What was typed, as an http(s) address. A tile opens in a normal tab, so nothing else is allowed through. */
export function parseToolLink(input: string): string {
	const url = new URL(normalizeLink(input))
	if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new InvalidLinkError(`not a web link: ${JSON.stringify(input)}`)
	return url.href
}

/** The name a tile shows when none was typed: the site's host. */
export function defaultTitle(url: string): string {
	return new URL(url).hostname.replace(/^www\./, '')
}
