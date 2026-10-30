import type { ReactNode } from 'react'

export interface SettingRowShellProps {
	readonly title: string
	readonly detail?: string | undefined
	readonly warning?: boolean
	readonly children?: ReactNode
}

/** A flat settings row: title and detail on the left, the control on the right (Krackn `SettingsRow`). */
export function SettingRowShell({ title, detail, warning = false, children }: SettingRowShellProps) {
	return (
		<div className="setting-row">
			<div className="setting-row-text">
				<div className="setting-row-title">{title}</div>
				{detail ? <div className={warning ? 'setting-row-detail warning' : 'setting-row-detail'}>{detail}</div> : null}
			</div>
			{children}
		</div>
	)
}
