import { useEffect, useState } from 'react'
import type { Notice } from '@shared/ipc'
import { zing } from '../api'
import { StackDropdownView } from '../chrome/tabs/StackDropdownView'
import { TabOverviewView } from '../overview/TabOverviewView'

/** The notice layer: one short message near the bottom of the page. */
function Notices() {
	const [notice, setNotice] = useState<Notice | null>(null)
	useEffect(() => zing.onNotice(setNotice), [])
	if (!notice) return null
	return (
		<div key={notice.id} className="notice" role="status">
			{notice.text}
		</div>
	)
}

/** Root of a transparent overlay view main stacks above the page: the notice layer, or the stack dropdown. */
export function Overlay() {
	if (location.hash === '#overlay-overview') return <TabOverviewView />
	return location.hash === '#overlay-stack' ? <StackDropdownView /> : <Notices />
}
