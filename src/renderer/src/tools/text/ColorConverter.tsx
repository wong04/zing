import { useState } from 'react'
import { convertColor } from '@shared/devtools/colorConverter'
import { InfoBanner, PillField } from '../../components'
import { ResultRow, ToolPage } from '../parts'
import './text.css'

export function ColorConverter() {
	const [input, setInput] = useState('')
	const colour = input.trim() === '' ? null : convertColor(input)
	return (
		<ToolPage subtitle="Hex or rgb() in, every notation out.">
			<PillField label="Colour" value={input} onChange={setInput} placeholder="#ff0000 or rgb(255, 0, 0)" spellCheck={false} />
			{input.trim() !== '' && !colour ? <InfoBanner kind="warning">That is not a hex or rgb() colour.</InfoBanner> : null}
			{colour ? (
				<>
					<div className="color-swatch" style={{ background: colour.hex }} />
					<div className="tool-rows">
						<ResultRow label="Hex" value={colour.hex} testId="color-hex" />
						<ResultRow label="RGB" value={colour.rgb} testId="color-rgb" />
						<ResultRow label="RGB (0-1)" value={colour.rgbFloat} testId="color-rgb-float" />
						<ResultRow label="HSL" value={colour.hsl} testId="color-hsl" />
						<ResultRow label="CMYK" value={colour.cmyk} testId="color-cmyk" />
					</div>
				</>
			) : null}
		</ToolPage>
	)
}
