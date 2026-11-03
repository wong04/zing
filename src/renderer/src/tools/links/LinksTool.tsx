import { useState } from 'react'
import { InvalidLinkError } from '@shared/browser/linkCleaner'
import { defaultTitle, parseToolLink } from '@shared/toolbox/links'
import { zing } from '../../api'
import { CloseGlyph, InfoBanner, PillButton, PillField } from '../../components'
import { useSettings } from '../../settings/useSettings'
import './links.css'

class NoActiveTabError extends Error {
	override name = 'NoActiveTabError'
}

/** The stored address is checked again here: settings.json can be edited by hand to hold something a tile must never open. */
async function openInNewTab(stored: string): Promise<void> {
	const url = parseToolLink(stored)
	await zing.runCommand('tab.new')
	const { activeId } = await zing.getTabs()
	if (activeId === undefined) throw new NoActiveTabError('A new tab was opened but none is active')
	await zing.navigateTab(activeId, url)
}

/** The Links page: tiles that open a site in a normal tab, which the user can add to and remove from. */
export function LinksTool() {
	const handle = useSettings()
	const [name, setName] = useState('')
	const [address, setAddress] = useState('')
	const [problem, setProblem] = useState<string | null>(null)
	if (!handle) return null
	const links = handle.settings.toolLinks

	const open = (url: string): void => {
		openInNewTab(url).then(
			() => setProblem(null),
			(error: unknown) => setProblem(error instanceof Error ? error.message : String(error)),
		)
	}

	const add = (): void => {
		let url: string
		try {
			url = parseToolLink(address)
		} catch (error) {
			if (!(error instanceof InvalidLinkError)) throw error
			setProblem(error.message)
			return
		}
		if (links.some((link) => link.url === url)) {
			setProblem('That link is already here.')
			return
		}
		handle.set('toolLinks', [...links, { title: name.trim() === '' ? defaultTitle(url) : name.trim(), url }])
		setName('')
		setAddress('')
		setProblem(null)
	}

	return (
		<div className="links-tool">
			<p className="links-note">Sites here sign you out when Zing closes, unless you turn on Stay signed in to sites in Settings.</p>
			<div className="links-tiles">
				{links.map((link) => (
					<div key={link.url} className="links-tile-wrap">
						<button type="button" className="links-tile" data-testid="link-tile" title={link.url} onClick={() => open(link.url)}>
							{link.title}
						</button>
						<button
							type="button"
							className="links-remove"
							aria-label={`Remove ${link.title}`}
							onClick={() => handle.set('toolLinks', links.filter((other) => other.url !== link.url))}
						>
							<CloseGlyph size={10} />
						</button>
					</div>
				))}
			</div>
			<div className="links-add">
				<PillField value={name} onChange={setName} placeholder="Name (optional)" aria-label="Link name" />
				<PillField value={address} onChange={setAddress} onSubmit={add} placeholder="Address" aria-label="Link address" />
				<PillButton size="small" onClick={add}>
					Add link
				</PillButton>
			</div>
			{problem === null ? null : <InfoBanner kind="warning">{problem}</InfoBanner>}
		</div>
	)
}
