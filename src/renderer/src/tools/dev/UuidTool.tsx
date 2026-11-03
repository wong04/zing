import { useState } from 'react'
import { makeUUIDs } from '@shared/devtools/uuid'
import { PillButton, Toggle } from '../../components'
import { CopyButton, DevTool } from './parts'

export function UuidTool() {
	const [uuids, setUuids] = useState(() => makeUUIDs(1))
	const [lowercase, setLowercase] = useState(false)
	const shown = uuids.map((uuid) => (lowercase ? uuid.toLowerCase() : uuid))

	return (
		<DevTool>
			<div className="dev-row">
				<PillButton onClick={() => setUuids(makeUUIDs(5))}>Generate 5</PillButton>
				<span className="dev-toggle">
					<Toggle checked={lowercase} onChange={setLowercase} aria-label="Lowercase" />
					Lowercase
				</span>
			</div>
			<div className="dev-list">
				{shown.map((uuid, index) => (
					<div key={`${index}-${uuid}`} className="dev-result">
						<span className="dev-result-value mono" data-testid="uuid-row">
							{uuid}
						</span>
						<CopyButton text={uuid} label="UUID" />
					</div>
				))}
			</div>
			<div className="dev-row">
				<CopyButton text={shown.join('\n')} label="all UUIDs" />
				<span className="dev-label">Copy all</span>
			</div>
		</DevTool>
	)
}
