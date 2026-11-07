// Whether a sign-in worked, which decides if Zing offers to save it. Source of truth for host/Zing.Core/Vault/SignInWatch.cs
// (SignInOutcomes), held to it by signInOutcome.vectors.json.

/** What the page channel accepts of a submit (Ruling 33), in UTF-16 code units. The script keeps to them; the host enforces them. */
export const signInLimits = { usernameMax: 512, passwordMax: 1024, holdMs: 10_000, settleChecks: 5, watchMs: 120_000 } as const

/**
 * `success`: the page the browser landed on, in the origin the form was submitted from, has no visible password box.
 * `failed`: that page shows a password box again, so the sign-in page came back. `pending`: it landed in another origin
 * (a redirect on the way, or somewhere Zing was never asked about), which says nothing either way; the held sign-in waits
 * for the next landing or for its time to run out.
 */
export type SignInOutcome = 'success' | 'failed' | 'pending'

/**
 * Ruling 37: for a single-page sign-in, `hasVisiblePasswordBox` is false only once the submitted password field has left the
 * document; a hidden, disabled or covered field still counts as the form being there. A prompt already on screen is
 * withdrawn when the form comes back in its origin: the sign-in failed after all.
 */
export function promptWithdrawn(promptOrigin: string, landedOrigin: string, hasVisiblePasswordBox: boolean): boolean {
	return landedOrigin === promptOrigin && hasVisiblePasswordBox
}

export function signInOutcome(submittedOrigin: string, landedOrigin: string, hasVisiblePasswordBox: boolean): SignInOutcome {
	if (landedOrigin !== submittedOrigin) return 'pending'
	return hasVisiblePasswordBox ? 'failed' : 'success'
}
