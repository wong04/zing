import { createContext, useContext, useEffect, useState } from 'react'
import { vaultKeepsData, type LoginSummary, type NeverSave, type VaultState } from '@shared/vault/vault'
import { zing } from '../api'
import { followHostState } from '../hostState'
import { setVaultKeepsData } from './privacy'

export interface VaultData {
	readonly state: VaultState
	/** Logins in Recently deleted too. Empty while the vault is off. */
	readonly summaries: readonly LoginSummary[]
	readonly neverSaves: readonly NeverSave[]
}

/** The vault as the Settings sheet shows it, or undefined until the host has answered. Neither list asks Windows Hello. */
export function useVaultData(): VaultData | undefined {
	const [state, setState] = useState<VaultState>()
	const [lists, setLists] = useState<{ summaries: LoginSummary[]; neverSaves: NeverSave[] }>()

	useEffect(() => followHostState(zing.onVaultState, zing.getVaultState, setState), [])

	useEffect(() => {
		if (!state) return
		if (!state.enabled) {
			setVaultKeepsData(false)
			return
		}
		let current = true
		void Promise.all([zing.vaultSummaries(), zing.neverSaves()]).then(([summaries, neverSaves]) => {
			if (!current) return
			// Before the render, so the privacy banner drawn with this data already reads it.
			setVaultKeepsData(vaultKeepsData(summaries, neverSaves))
			setLists({ summaries, neverSaves })
		})
		return () => {
			current = false
		}
	}, [state])

	if (!state) return undefined
	if (!state.enabled) return { state, summaries: [], neverSaves: [] }
	return lists && { state, ...lists }
}

const VaultDataContext = createContext<VaultData | undefined>(undefined)

export const VaultDataProvider = VaultDataContext.Provider

export function useVault(): VaultData | undefined {
	return useContext(VaultDataContext)
}
