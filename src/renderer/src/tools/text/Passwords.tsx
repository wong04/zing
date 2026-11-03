import { useState } from 'react'
import { generatePassword } from '@shared/devtools/password'
import { PillButton, PillSlider, Toggle } from '../../components'
import { CopyButton, ToolPage } from './parts'
import './text.css'

const count = 5

export default function Passwords() {
	const [length, setLength] = useState(20)
	const [uppercase, setUppercase] = useState(true)
	const [digits, setDigits] = useState(true)
	const [symbols, setSymbols] = useState(true)
	const [passwords, setPasswords] = useState<readonly string[]>([])

	const generate = (): void => {
		setPasswords(Array.from({ length: count }, () => generatePassword({ length, uppercase, digits, symbols })))
	}

	return (
		<ToolPage subtitle="Generated here with the system random source. Nothing leaves this computer.">
			<div className="text-tool-controls">
				<span>Length</span>
				<PillSlider aria-label="Length" min={8} max={64} step={1} value={length} onChange={setLength} />
				<span className="text-tool-number">{length}</span>
			</div>
			<div className="toggle-row">
				Uppercase letters <Toggle aria-label="Uppercase letters" checked={uppercase} onChange={setUppercase} />
			</div>
			<div className="toggle-row">
				Digits <Toggle aria-label="Digits" checked={digits} onChange={setDigits} />
			</div>
			<div className="toggle-row">
				Symbols <Toggle aria-label="Symbols" checked={symbols} onChange={setSymbols} />
			</div>
			<p className="text-tool-subtitle">Easily misread characters (0/O, 1/l/I) are left out so a password is safe to read aloud.</p>
			<PillButton onClick={generate}>Generate {count}</PillButton>
			<div className="text-tool-rows">
				{passwords.map((password, index) => (
					<div className="output-row" key={index}>
						<span className="output-row-value" data-testid="password">
							{password}
						</span>
						<CopyButton text={password} label="password" />
					</div>
				))}
			</div>
		</ToolPage>
	)
}
