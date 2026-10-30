import { useCallback, useEffect, useState } from 'react'
import type { Settings, SettingKey } from '@shared/settings'
import { zing } from '../api'

export interface SettingsHandle {
	readonly settings: Settings
	readonly set: <K extends SettingKey>(key: K, value: Settings[K]) => void
}

/** Live settings from main; undefined until the first read arrives. Writes show at once and main confirms. */
export function useSettings(): SettingsHandle | undefined {
	const [settings, setSettings] = useState<Settings>()
	useEffect(() => {
		const off = zing.onSettingsChanged(setSettings)
		void zing.getSettings().then(setSettings)
		return off
	}, [])
	const set = useCallback(<K extends SettingKey>(key: K, value: Settings[K]): void => {
		setSettings((prev) => (prev ? { ...prev, [key]: value } : prev))
		void zing.setSetting(key, value)
	}, [])
	return settings ? { settings, set } : undefined
}
