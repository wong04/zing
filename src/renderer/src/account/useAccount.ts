import { useEffect, useState } from 'react'
import type { AccountState } from '@shared/shell/shellState'
import { zing } from '../api'

/** Live account state from main; undefined until the first read arrives. */
export function useAccount(): AccountState | undefined {
	const [state, setState] = useState<AccountState>()
	useEffect(() => {
		const off = zing.onAccountState(setState)
		void zing.getAccount().then(setState)
		return off
	}, [])
	return state
}

export function initials(name: string): string {
	const letters = name
		.split(/\s+/)
		.filter(Boolean)
		.slice(0, 2)
		.map((word) => word.charAt(0))
	return letters.length === 0 ? '?' : letters.join('').toUpperCase()
}
