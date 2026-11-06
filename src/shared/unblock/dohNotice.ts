// What Open a Link and Diagnose say about the encrypted DNS provider, which is an outside service they use without asking.

/** Shown before a run. The provider is the one chosen in Settings, by the name Settings shows. */
export function dohNotice(provider: string): string {
	return `To check the site, Zing asks ${provider}, the encrypted DNS provider chosen in Settings, for its address, so ${provider} receives the site's name. Names on your own network are never sent.`
}

/** Shown when the first use of the reader relay or the archive was declined. */
export function declinedNotice(provider: string): string {
	return `Stopped before the reader relay and the archive. Nothing was sent to the reader relay or the archive. ${provider} had already been asked for the site's name, as the note above says. Run it again and say yes if you change your mind.`
}
