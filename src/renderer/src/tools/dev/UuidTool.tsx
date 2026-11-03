import { useState } from 'react'
import { makeUUIDs } from '@shared/devtools/uuid'
import { PillButton, Toggle } from '../../components'
import { CopyButton, ToolPage } from '../parts'

export function UuidTool() {
	const [uuids, setUuids] = useState(() => makeUUIDs(1))
	const [lowercase, setLowercase] = useState(false)
	const shown = uuids.map((uuid) => (lowercase ? uuid.toLowerCase() : uuid))

	return (
		<ToolPage>
			<div className="dev-row">
				<PillButton onClick={() => setUuids(makeUUIDs(5))}>Generate 5</PillButton>
				<span className="dev-toggle">
					<Toggle checked={lowercase} onChange={setLowercase} aria-label="Lowercase" />
					Lowercase
				</span>
			</div>
			<div className="tool-rows">
				{shown.map((uuid, index) => (
					<div key={`${index}-${uuid}`} className="tool-result">
						<span className="tool-result-value mono" data-testid="uuid-row">
							{uuid}
						</span>
						<CopyButton value={uuid} label="UUID" />
					</div>
				))}
			</div>
			<div className="dev-row">
				<CopyButton value={shown.join('\n')} label="all UUIDs" />
				<span className="tool-label">Copy all</span>
			</div>
		</ToolPage>
	)
}
