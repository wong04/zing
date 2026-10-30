const OPENABLE_PROTOCOLS: ReadonlySet<string> = new Set(['http:', 'https:'])

/** Windows hands a quoted argument through as-is when the shell's quoting reaches the process raw. */
function unquote(token: string): string {
	const trimmed = token.trim()
	const pair = /^(["'])(.*)\1$/.exec(trimmed)
	if (pair) return (pair[2] ?? '').trim()
	// A trailing backslash before a closing quote can leave one stray quote on either end.
	return trimmed.replace(/^["']|["']$/g, '').trim()
}

function openableUrl(token: string): string | undefined {
	const candidate = unquote(token)
	if (candidate.startsWith('-') || !URL.canParse(candidate)) return undefined
	return OPENABLE_PROTOCOLS.has(new URL(candidate).protocol) ? candidate : undefined
}

/**
 * The web addresses in a process's command line, in order. Switches, the
 * executable and the app path are skipped; only http(s) links qualify, so a
 * `file:` or custom-scheme argument can't be smuggled in through a link click.
 */
export function parseLaunchUrls(argv: readonly string[]): string[] {
	const urls: string[] = []
	for (const token of argv) {
		const url = openableUrl(token)
		if (url !== undefined) urls.push(url)
	}
	return urls
}

/**
 * Links that arrive before any window can take them (at launch, or from a
 * second instance during startup) wait here, as in Krackn's `LinkInbox`.
 */
export class LinkInbox {
	readonly #pending: string[] = []
	#sink: ((url: string) => void) | undefined

	push(url: string): void {
		if (this.#sink) this.#sink(url)
		else this.#pending.push(url)
	}

	/** Hands over everything queued so far, then every later link directly. */
	attach(sink: (url: string) => void): void {
		this.#sink = sink
		for (const url of this.#pending.splice(0)) sink(url)
	}

	get pendingCount(): number {
		return this.#pending.length
	}
}
