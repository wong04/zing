import { useEffect, useState } from 'react'
import type { Notice } from '@shared/ipc'
import { zing } from '../api'
import { StackDropdownView } from '../chrome/tabs/StackDropdownView'
import { TabOverviewView } from '../overview/TabOverviewView'
import { useAccentTheme } from '../settings/useAccentTheme'

/** A page failed like a block would make it. Both buttons only open a tool with the address filled in; nothing is tried until a click there. */
function UnblockOffer({ address }: { address: string }) {
	return (
		<div className="notice offer" role="group" aria-label="This page didn't load" data-testid="unblock-offer">
			<div className="offer-text">
				<strong>This page didn't load</strong>
				<span className="offer-address" title={address} data-testid="offer-address">
					{address}
				</span>
			</div>
			<div className="offer-actions">
				<button type="button" className="pill-button filled small" onClick={() => void zing.chooseOffer('openLink')}>
					Open a Link
				</button>
				<button type="button" className="pill-button outlined small" onClick={() => void zing.chooseOffer('diagnose')}>
					Diagnose
				</button>
				<button type="button" className="pill-icon-button" aria-label="Dismiss" title="Dismiss" onClick={() => void zing.dismissOffer()}>
					×
				</button>
			</div>
		</div>
	)
}

/** The notice layer: one short message near the bottom of the page, or the offer after a page failed to load. */
function Notices() {
	const [notice, setNotice] = useState<Notice | null>(null)
	useEffect(() => zing.onNotice(setNotice), [])
	if (!notice) return null
	if (notice.offer) return <UnblockOffer key={notice.id} address={notice.offer.address} />
	return (
		<div key={notice.id} className="notice" role="status">
			{notice.text}
		</div>
	)
}

/** Root of a transparent overlay view main stacks above the page: the notice layer, or the stack dropdown. */
export function Overlay() {
	useAccentTheme()
	if (location.hash === '#overlay-overview') return <TabOverviewView />
	return location.hash === '#overlay-stack' ? <StackDropdownView /> : <Notices />
}
