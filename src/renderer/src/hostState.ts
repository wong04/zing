/**
 * Follows a piece of the host's state: one fetch to start, then every push. The host can send a push
 * between working out a fetch's reply and sending it, so a reply may arrive after a newer push; applied, it
 * would wind the state back (the chrome once lost its active tab that way, so typing an address did nothing).
 * The host pushes every change, so once a push has arrived the reply has nothing newer to add and is dropped.
 * Returns the function that stops following.
 */
export function followHostState<T>(subscribe: (listener: (state: T) => void) => () => void, fetch: () => Promise<T>, apply: (state: T) => void): () => void {
	let pushed = false
	const unsubscribe = subscribe((state) => {
		pushed = true
		apply(state)
	})
	void fetch().then((state) => {
		if (!pushed) apply(state)
	})
	return unsubscribe
}
