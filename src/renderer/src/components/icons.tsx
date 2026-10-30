import type { ReactNode } from 'react'

export interface GlyphProps {
	readonly size?: number
}

function Glyph({ size = 16, children }: GlyphProps & { children: ReactNode }) {
	return (
		<svg
			className="glyph"
			viewBox="0 0 16 16"
			width={size}
			height={size}
			fill="none"
			stroke="currentColor"
			strokeWidth="1.4"
			strokeLinecap="round"
			strokeLinejoin="round"
			aria-hidden="true"
		>
			{children}
		</svg>
	)
}

export const CheckIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="m3.5 8.5 3 3 6-7" />
	</Glyph>
)

export const CloseGlyph = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="m4.5 4.5 7 7M11.5 4.5l-7 7" />
	</Glyph>
)

export const MinusIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M4 8h8" />
	</Glyph>
)

export const ChevronUpDownIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="m5 6.5 3-3 3 3M5 9.5l3 3 3-3" />
	</Glyph>
)

export const SidebarIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<rect x="2" y="3" width="12" height="10" rx="2" />
		<path d="M6 3v10" />
	</Glyph>
)

export const GlobeIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<circle cx="8" cy="8" r="5.5" />
		<path d="M2.5 8h11M8 2.5c2 2 2 9 0 11M8 2.5c-2 2-2 9 0 11" />
	</Glyph>
)

export const GridIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<rect x="2.5" y="2.5" width="4.5" height="4.5" rx="1" />
		<rect x="9" y="2.5" width="4.5" height="4.5" rx="1" />
		<rect x="2.5" y="9" width="4.5" height="4.5" rx="1" />
		<rect x="9" y="9" width="4.5" height="4.5" rx="1" />
	</Glyph>
)

export const GearIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<circle cx="8" cy="8" r="2" />
		<path d="M8 1.8v1.6M8 12.6v1.6M1.8 8h1.6M12.6 8h1.6M3.6 3.6l1.1 1.1M11.3 11.3l1.1 1.1M3.6 12.4l1.1-1.1M11.3 4.7l1.1-1.1" />
	</Glyph>
)

export const WindowIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<rect x="2" y="3" width="12" height="10" rx="2" />
		<path d="M2 6h12" />
	</Glyph>
)

export const TabsIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<rect x="2.5" y="5" width="9" height="8" rx="1.8" />
		<path d="M5 3h6.5A2 2 0 0 1 13.5 5v6" />
	</Glyph>
)

export const FolderIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M2 4.5A1.5 1.5 0 0 1 3.5 3h2.6l1.4 1.5h5A1.5 1.5 0 0 1 14 6v5.5a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 11.5z" />
	</Glyph>
)

export const HandIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M5.5 8V4a1 1 0 0 1 2 0v3.5M7.5 7V3a1 1 0 0 1 2 0v4M9.5 7V4a1 1 0 0 1 2 0v5.5a4 4 0 0 1-4 4h-.3A3.5 3.5 0 0 1 4 11l-1-2.2a1 1 0 0 1 1.7-1l.8 1.2" />
	</Glyph>
)

export const ShieldIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M8 1.8 3 3.6v4.1c0 3 2 5.2 5 6.5 3-1.3 5-3.5 5-6.5V3.6z" />
	</Glyph>
)

export const ShieldCheckIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M8 1.8 3 3.6v4.1c0 3 2 5.2 5 6.5 3-1.3 5-3.5 5-6.5V3.6z" />
		<path d="m5.8 8 1.7 1.7L10.4 6.6" />
	</Glyph>
)

export const ShieldAlertIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M8 1.8 3 3.6v4.1c0 3 2 5.2 5 6.5 3-1.3 5-3.5 5-6.5V3.6z" />
		<path d="M8 5.6v3M8 10.8h.01" />
	</Glyph>
)

export const SlidersIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M2.5 5h6M11.5 5h2M2.5 11h2M7.5 11h6" />
		<circle cx="10" cy="5" r="1.5" />
		<circle cx="6" cy="11" r="1.5" />
	</Glyph>
)

export const PersonIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<circle cx="8" cy="5.8" r="2.6" />
		<path d="M3 13.5c.6-2.4 2.6-3.7 5-3.7s4.4 1.3 5 3.7" />
	</Glyph>
)

export const InfoIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<circle cx="8" cy="8" r="5.6" />
		<path d="M8 7.4v3.4M8 5.4h.01" />
	</Glyph>
)

export const LockIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<rect x="3.5" y="7" width="9" height="6.5" rx="1.6" />
		<path d="M5.5 7V5.5a2.5 2.5 0 0 1 5 0V7" />
	</Glyph>
)

export const MailIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<rect x="2" y="3.5" width="12" height="9" rx="1.8" />
		<path d="m2.5 5 5.5 4 5.5-4" />
	</Glyph>
)

export const TextIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M3 4.5V3h10v1.5M8 3v10M6 13h4" />
	</Glyph>
)

export const LinkIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M6.8 9.2a2.6 2.6 0 0 0 3.7 0l2-2a2.6 2.6 0 0 0-3.7-3.7l-.7.7M9.2 6.8a2.6 2.6 0 0 0-3.7 0l-2 2a2.6 2.6 0 0 0 3.7 3.7l.7-.7" />
	</Glyph>
)

export const FileGlyph = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M4 2h5l3 3v8.2a.8.8 0 0 1-.8.8H4.8a.8.8 0 0 1-.8-.8z" />
		<path d="M9 2v3h3" />
	</Glyph>
)

export const ArrowsIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M3 5.5h9l-2.5-2.5M13 10.5H4l2.5 2.5" />
	</Glyph>
)

export const DownloadIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M8 2.5v7.5M4.8 7.2 8 10.4l3.2-3.2M3 13h10" />
	</Glyph>
)

export const WaveIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M2 8h1.5l1.5-4 2 8 2-6 1.5 2H14" />
	</Glyph>
)

export const CodeIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="m5.5 4.5-3 3.5 3 3.5M10.5 4.5l3 3.5-3 3.5" />
	</Glyph>
)

export const CalculatorIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<rect x="3.5" y="2" width="9" height="12" rx="1.8" />
		<path d="M5.5 5h5M5.8 8h.01M8 8h.01M10.2 8h.01M5.8 10.5h.01M8 10.5h.01M10.2 10.5h.01" />
	</Glyph>
)

export const PaletteIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M8 2.2a5.8 5.8 0 1 0 0 11.6c1 0 1.4-.7 1.1-1.4-.4-.9.1-1.8 1.1-1.8h1.2A2.4 2.4 0 0 0 14 8.2 5.8 5.8 0 0 0 8 2.2z" />
		<path d="M5 7.6h.01M7 5h.01M10 5.6h.01" />
	</Glyph>
)

export const KeyIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<circle cx="5.2" cy="10.8" r="2.4" />
		<path d="m7 9 6-6M11 5l1.6 1.6" />
	</Glyph>
)

export const ToolIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<path d="M10.5 2.5a3.2 3.2 0 0 0-2.9 4.4L2.8 11.7a1.3 1.3 0 0 0 1.8 1.8l4.8-4.8a3.2 3.2 0 0 0 4.4-2.9l-2 1.5-1.6-.4-.4-1.6z" />
	</Glyph>
)

export const FolderOpenIcon = FolderIcon

export const CopyIcon = (p: GlyphProps) => (
	<Glyph {...p}>
		<rect x="5.5" y="5.5" width="8" height="8" rx="1.6" />
		<path d="M10.5 5.5V4a1.5 1.5 0 0 0-1.5-1.5H4A1.5 1.5 0 0 0 2.5 4v5A1.5 1.5 0 0 0 4 10.5h1.5" />
	</Glyph>
)
