/** What the Network & VPN page shows of the encrypted-DNS script (host: DnsPreview). */
export interface DnsPreview {
	/** Encrypted DNS was set up by Zing and its previous settings are saved: the script is Undo's. */
	readonly applied: boolean
	/** The dohProvider setting value the script is for. */
	readonly provider: string
	readonly providerName: string
	/** The adapters the script changes (Apply) or restores (Undo), with hidden characters in a name spelled out. */
	readonly adapters: readonly string[]
	/** The exact text that runs as administrator. Not for display. */
	readonly script: string
	/** `script` with hidden characters spelled out as \uXXXX: what the page shows. */
	readonly scriptShown: string
	/** Names `script`; handed back to apply or undo, which run only that script. */
	readonly token: string
	/** Where the previous settings are kept until Undo. */
	readonly backupPath: string
	/** When they were saved (ISO), once applied; absent before. */
	readonly savedAt?: string
	/** This Windows has the encrypted-DNS commands (Windows 11). Without them Apply is not offered. */
	readonly dohSupported: boolean
	/** Applied only: each adapter with the servers Undo will set (or "automatic (DHCP)") and each encrypted-DNS entry it restores or removes. */
	readonly changes: readonly string[]
}

/** `changed`: the script is not the one that was shown, nothing ran, `preview` is the one there now. `declined`: the admin prompt was refused. */
export interface DnsOutcome {
	readonly status: 'applied' | 'undone' | 'declined' | 'changed'
	readonly preview?: DnsPreview
}
