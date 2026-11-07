/** How long a copied password stays on the clipboard before Zing clears it. */
export const CLIPBOARD_CLEAR_MS = 30_000

/**
 * Copies a password and clears the clipboard after {@link CLIPBOARD_CLEAR_MS}. The clear is unconditional: reading the clipboard
 * back to see whether it still holds the password needs a second permission, and a password left behind is the worse mistake.
 * Rejects when the clipboard refuses the write, so the caller can say so.
 */
export async function copySecret(secret: string): Promise<void> {
	await navigator.clipboard.writeText(secret)
	setTimeout(() => void navigator.clipboard.writeText(''), CLIPBOARD_CLEAR_MS)
}
