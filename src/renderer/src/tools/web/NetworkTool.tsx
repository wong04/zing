import { useEffect, useState } from 'react'
import type { DnsOutcome, DnsPreview } from '@shared/unblock/dnsSetup'
import { zing } from '../../api'
import { InfoBanner, PillButton, SectionCard } from '../../components'
import { ResultRow, TextArea, ToolPage } from '../parts'
import './web.css'

/** Stated plainly because it decides what the script is for: the browser engine ignored every encrypted-DNS switch that was tried. */
const engineNote =
	"Zing's own lookups (Open a Link and Diagnose) use the encrypted DNS provider chosen in Settings. Pages in your tabs don't: the browser engine ignored every encrypted DNS setting we tried (checked on runtime 156), so pages use Windows' DNS. This script is how pages get encrypted DNS: it changes Windows, for every app on this PC."

/** What each row of "What beats what" says, reworded from Krackn for Windows. `local` rows are handled by Zing itself. */
const beats: readonly { readonly block: string; readonly fix: string; readonly local: boolean }[] = [
	{ block: 'DNS-level block', fix: "Encrypted DNS (the script above), or Open a Link's DoH method", local: true },
	{ block: 'IP-level block', fix: "Open a Link's reader relay, or a VPN", local: false },
	{ block: 'TLS/SNI inspection', fix: "Open a Link's reader relay, or a VPN", local: false },
	{ block: 'Full proxy with TLS interception', fix: "Only a VPN, if it isn't blocked too", local: false },
]

const messageOf = (error: unknown): string => (error instanceof Error ? error.message : String(error))

function noticeFor(outcome: DnsOutcome, shown: DnsPreview): string {
	switch (outcome.status) {
		case 'applied':
			return `Encrypted DNS is set up on ${shown.adapters.join(', ')}. Windows reported no errors. Pages may need a reload.`
		case 'undone':
			return 'The settings saved when you applied were put back. Windows reported no errors.'
		case 'declined':
			return 'Nothing changed. Windows asked for permission and it was refused.'
		case 'changed':
			return 'The script changed since it was shown, so nothing ran. Here is the new one: read it, then press the button again.'
	}
}

/** Network & VPN (X5): encrypted DNS for the whole PC by a script shown before it runs, a short VPN guide, and what beats what. */
export function NetworkTool() {
	const [preview, setPreview] = useState<DnsPreview | null>(null)
	const [notice, setNotice] = useState<string | null>(null)
	const [problem, setProblem] = useState<string | null>(null)
	const [busy, setBusy] = useState(false)

	useEffect(() => {
		let current = true
		zing.previewDns().then(
			(shown) => current && setPreview(shown),
			(error: unknown) => current && setProblem(messageOf(error)),
		)
		return () => {
			current = false
		}
	}, [])

	const refresh = async (): Promise<void> => {
		try {
			setPreview(await zing.previewDns())
		} catch (error) {
			setPreview(null)
			setProblem((earlier) => (earlier === null ? messageOf(error) : `${earlier} ${messageOf(error)}`))
		}
	}

	const run = async (change: (token: string) => Promise<DnsOutcome>): Promise<void> => {
		if (preview === null) return
		setBusy(true)
		setProblem(null)
		setNotice(null)
		try {
			const outcome = await change(preview.token)
			setNotice(noticeFor(outcome, preview))
			if (outcome.status === 'declined') return
			if (outcome.status !== 'changed') return await refresh()
			if (outcome.preview === undefined) throw new Error('The host said the script changed but sent no new one')
			setPreview(outcome.preview)
		} catch (error) {
			setProblem(messageOf(error))
			// A failed script keeps the old settings for Undo, so what the page offers may now be different.
			await refresh()
		} finally {
			setBusy(false)
		}
	}

	return (
		<ToolPage subtitle="Encrypted DNS for this whole PC, and how to get a VPN.">
			<SectionCard title="Encrypted DNS for this PC" compact>
				<p className="diagnose-detail" data-testid="dns-engine-note">
					{engineNote}
				</p>
				{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
				{notice === null ? null : (
					<p className="diagnose-detail" role="status" data-testid="dns-notice">
						{notice}
					</p>
				)}
				{preview === null ? (
					problem === null ? (
						<p className="diagnose-detail">Reading your network adapters…</p>
					) : null
				) : !preview.applied && !preview.dohSupported ? (
					<p className="diagnose-detail" data-testid="dns-unsupported">
						This needs Windows 11. This version of Windows has no encrypted DNS commands, so there is nothing here to apply.
					</p>
				) : (
					<>
						<p className="diagnose-detail">
							{preview.applied
								? `Encrypted DNS (${preview.providerName}) was set by Zing. Undo restores the settings saved when you applied, for: `
								: `${preview.providerName} (change it in Settings) will be set on: `}
							<span data-testid="dns-adapters">{preview.adapters.join(', ')}</span>
							{preview.applied ? '' : '. Connected physical adapters only; VPN and virtual adapters are left alone.'}
						</p>
						{preview.changes.length === 0 ? null : (
							<>
								<p className="diagnose-detail">Undo will set exactly this:</p>
								<ul className="net-undo-list" data-testid="dns-undo-list">
									{preview.changes.map((line) => (
										<li key={line}>{line}</li>
									))}
								</ul>
							</>
						)}
						<TextArea label="Script that runs as administrator" value={preview.scriptShown} readOnly rows={Math.min(14, preview.scriptShown.split('\n').length)} />
						<p className="diagnose-detail" data-testid="dns-keeps-note">
							Zing keeps nothing by default, but undoing a change to Windows needs your previous DNS settings, so they are kept in {preview.backupPath} until you Undo. Windows asks for permission first. If your network blocks {preview.providerName}'s servers, sites won't load until you Undo.
						</p>
						<div>
							{preview.applied ? (
								<PillButton disabled={busy} onClick={() => void run(zing.undoDns)}>
									Undo
								</PillButton>
							) : (
								<PillButton disabled={busy} onClick={() => void run(zing.applyDns)}>
									Apply
								</PillButton>
							)}
						</div>
					</>
				)}
			</SectionCard>

			<SectionCard title="VPN" compact>
				<div className="net-guide" data-testid="vpn-guide">
					<p className="diagnose-detail">
						Zing can't be a VPN, but it can help you use one. A VPN is a server somewhere else that forwards your traffic; an app is only the part that dials it. Two ways to get one: a provider such as Mullvad, Proton or IVPN, which hands you a WireGuard config for a few dollars a month, or your own server, a small VPS running WireGuard (more setup, no subscription).
					</p>
					<p className="diagnose-detail">
						A connected VPN usually uses its own DNS, so the encrypted DNS setting above doesn't apply to traffic that goes through it.
					</p>
					<p className="diagnose-detail">
						Bringing a tunnel up changes your routing and needs administrator rights, so you run it yourself: in the WireGuard app choose Import tunnel(s) from file, or from an administrator PowerShell:
					</p>
					<div className="tool-rows">
						<ResultRow label="Install WireGuard" value="winget install WireGuard.WireGuard" />
						<ResultRow label="Bring a tunnel up" value={'& "C:\\Program Files\\WireGuard\\wireguard.exe" /installtunnelservice C:\\path\\to\\your.conf'} />
						<ResultRow label="Take it down" value={'& "C:\\Program Files\\WireGuard\\wireguard.exe" /uninstalltunnelservice your'} />
					</div>
				</div>
			</SectionCard>

			<SectionCard title="What beats what" compact>
				<ul className="net-beats">
					{beats.map((row) => (
						<li key={row.block} data-testid="beats-row" data-local={row.local}>
							<span className="net-beats-block">{row.block}</span>
							<span className="diagnose-detail">{row.fix}</span>
						</li>
					))}
				</ul>
				<p className="diagnose-detail">Diagnose tells you which one a site is.</p>
			</SectionCard>
		</ToolPage>
	)
}
