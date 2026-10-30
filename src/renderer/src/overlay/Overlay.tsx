import { useEffect, useState } from 'react'
import type { Notice } from '@shared/ipc'
import { zing } from '../api'

/** Root of the transparent overlay view main stacks above the page. */
export function Overlay() {
	const [notice, setNotice] = useState<Notice | null>(null)
	useEffect(() => zing.onNotice(setNotice), [])
	if (!notice) return null
	return (
		<div key={notice.id} className="notice" role="status">
			{notice.text}
		</div>
	)
}
