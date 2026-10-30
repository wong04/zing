import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import type { Settings, SettingKey } from '@shared/settings'
import type { SettingsCategoryId } from '@shared/shell/shellState'
import { zing } from '../api'
import { FadingScroll, IconButton, PillField, PillMenu, PillSlider, SegmentedControl, Toggle } from '../components'
import {
	CloseGlyph,
	FolderIcon,
	GearIcon,
	GlobeIcon,
	GridIcon,
	HandIcon,
	InfoIcon,
	PersonIcon,
	ShieldIcon,
	SlidersIcon,
	TabsIcon,
} from '../components/icons'
import { CustomRow } from './CustomRows'
import { CATEGORIES, PANES, detailOf, isVisible, type SettingRow } from './rows'
import { SettingRowShell } from './SettingRowShell'
import type { SettingsHandle } from './useSettings'
import './settings.css'

const ICONS: Readonly<Record<SettingsCategoryId, () => React.JSX.Element>> = {
	general: () => <GearIcon size={15} />,
	browsing: () => <GlobeIcon size={15} />,
	newTab: () => <GridIcon size={15} />,
	tabs: () => <TabsIcon size={15} />,
	files: () => <FolderIcon size={15} />,
	privacy: () => <HandIcon size={15} />,
	security: () => <ShieldIcon size={15} />,
	advanced: () => <SlidersIcon size={15} />,
	account: () => <PersonIcon size={15} />,
	about: () => <InfoIcon size={15} />,
}

function control(row: SettingRow, settings: Settings, set: SettingsHandle['set']) {
	const value: unknown = settings[row.key as keyof Settings]
	const write = (next: unknown): void => set(row.key, next as never)
	const c = row.control
	switch (c.kind) {
		case 'toggle':
			return <Toggle aria-label={row.title} checked={value === true} onChange={write} />
		case 'segmented':
			return (
				<SegmentedControl
					aria-label={row.title}
					width={c.width}
					options={c.options}
					value={value as string | number | boolean}
					onChange={write}
				/>
			)
		case 'menu':
			return <PillMenu aria-label={row.title} options={c.options} value={value as string | number | boolean} onChange={write} />
		case 'text':
			return (
				<div style={{ width: c.width }}>
					<PillField aria-label={row.title} value={value as string} onChange={write} placeholder={c.placeholder} />
				</div>
			)
		case 'slider': {
			const n = Math.min(Math.max(value as number, c.min), c.max)
			return (
				<div className="control-slider">
					<PillSlider
						aria-label={row.title}
						min={c.min}
						max={c.max}
						step={c.step}
						value={n}
						valueText={c.unit ? `${n} ${c.unit}` : String(n)}
						onChange={write}
					/>
					<span className="row-value">{c.unit ? `${n} ${c.unit}` : n}</span>
				</div>
			)
		}
	}
}

function Pane({ category, handle, version }: { category: SettingsCategoryId; handle: SettingsHandle; version: string }) {
	const { settings, set } = handle
	return (
		<div className="pane">
			{PANES[category]
				.filter((item) => isVisible(item, settings))
				.map((item) =>
					item.type === 'custom' ? (
						<CustomRow key={item.id} id={item.id} handle={handle} version={version} />
					) : (
						<SettingRowShell
							key={item.key}
							title={item.title}
							detail={detailOf(item, settings)}
							warning={item.warning?.(settings) ?? false}
						>
							{control(item, settings, set)}
						</SettingRowShell>
					),
				)}
		</div>
	)
}

export interface SettingsSheetProps {
	readonly handle: SettingsHandle
	readonly initialCategory: SettingsCategoryId
	readonly version: string
}

/** The blurred-backdrop sheet (Krackn `SettingsSheet`): categories on the left, flat rows on the right. */
export function SettingsSheet({ handle, initialCategory, version }: SettingsSheetProps) {
	const [category, setCategory] = useState(initialCategory)
	const [shown, setShown] = useState(false)
	const sheet = useRef<HTMLDivElement>(null)
	const current = CATEGORIES.find((c) => c.id === category)

	useEffect(() => {
		const frame = requestAnimationFrame(() => setShown(true))
		sheet.current?.focus()
		return () => cancelAnimationFrame(frame)
	}, [])

	const close = (): void => void zing.closeSettings()
	const onKeyDown = (e: KeyboardEvent): void => {
		if (e.key !== 'Escape') return
		e.preventDefault()
		close()
	}
	const onNavKey = (e: KeyboardEvent): void => {
		const delta = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : 0
		if (delta === 0) return
		e.preventDefault()
		const index = CATEGORIES.findIndex((c) => c.id === category)
		const next = CATEGORIES[(index + delta + CATEGORIES.length) % CATEGORIES.length]
		if (next) setCategory(next.id)
	}

	return (
		<div className={shown ? 'sheet-backdrop shown' : 'sheet-backdrop'} onKeyDown={onKeyDown}>
			<div className="sheet-dismiss" onPointerDown={close} aria-hidden="true" />
			<div ref={sheet} className="sheet" role="dialog" aria-modal="true" aria-label="Settings" tabIndex={-1}>
				<nav className="sheet-nav" aria-label="Settings categories" onKeyDown={onNavKey}>
					<div className="sheet-nav-title">Settings</div>
					{CATEGORIES.map((c) => (
						<button
							key={c.id}
							type="button"
							className={c.id === category ? 'nav-row selected' : 'nav-row'}
							aria-current={c.id === category ? 'page' : undefined}
							onClick={() => setCategory(c.id)}
						>
							{ICONS[c.id]()}
							<span>{c.title}</span>
						</button>
					))}
				</nav>
				<FadingScroll className="sheet-content" aria-label={`${current?.title ?? ''} settings`}>
					<div className="sheet-content-inner" key={category}>
						<h2>{current?.title}</h2>
						<Pane category={category} handle={handle} version={version} />
					</div>
				</FadingScroll>
				<div className="sheet-close">
					<IconButton aria-label="Close settings (Esc)" size={28} onClick={close}>
						<CloseGlyph size={14} />
					</IconButton>
				</div>
			</div>
		</div>
	)
}

export type { SettingKey }
