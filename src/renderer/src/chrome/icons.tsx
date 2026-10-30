import type { ReactNode } from 'react'

function Glyph({ children }: { children: ReactNode }) {
	return (
		<svg
			className="glyph"
			viewBox="0 0 16 16"
			width="16"
			height="16"
			fill="none"
			stroke="currentColor"
			strokeWidth="1.5"
			strokeLinecap="round"
			strokeLinejoin="round"
			aria-hidden="true"
		>
			{children}
		</svg>
	)
}

export const BackIcon = () => (
	<Glyph>
		<path d="M10 3 5 8l5 5" />
	</Glyph>
)

export const ForwardIcon = () => (
	<Glyph>
		<path d="m6 3 5 5-5 5" />
	</Glyph>
)

export const ReloadIcon = () => (
	<Glyph>
		<path d="M13 8a5 5 0 1 1-1.5-3.6" />
		<path d="M13 2.5v3h-3" />
	</Glyph>
)

export const StopIcon = () => (
	<Glyph>
		<path d="m4 4 8 8M12 4l-8 8" />
	</Glyph>
)

export const CloseIcon = () => (
	<Glyph>
		<path d="m5 5 6 6M11 5l-6 6" />
	</Glyph>
)

export const PlusIcon = () => (
	<Glyph>
		<path d="M8 3v10M3 8h10" />
	</Glyph>
)

export const MoreIcon = () => (
	<Glyph>
		<path d="M3.5 8h.01M8 8h.01M12.5 8h.01" strokeWidth="2.25" />
	</Glyph>
)
