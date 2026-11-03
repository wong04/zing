import { useMemo, useState } from 'react'
import { PillButton, PillField, SectionCard, SegmentedControl } from '../../components'
import { AngleMode, evaluate, EvalError, format } from '@shared/math/expression'
import { angleOptions } from './angleOptions'
import { evalMessage } from './evalMessage'
import { CopyButton, ToolPage } from '../parts'
import './math.css'

type Preview = { readonly kind: 'result'; readonly text: string } | { readonly kind: 'error'; readonly message: string } | { readonly kind: 'empty' }

const keys = ['(', ')', '^', '√', 'π', 'e', '!', '%']
const HISTORY_LIMIT = 25

const supported: readonly (readonly [string, string])[] = [
	['Operators', '+  −  ×  ÷  ^  %  !  ( )'],
	['Functions', 'sin cos tan asin acos atan sinh cosh tanh'],
	['', 'sqrt √ cbrt ln log log2 exp abs round floor ceil sign deg rad'],
	['Constants', 'pi  π  e  tau'],
	['Shorthand', '2(3+4) = 14 · 3pi · 1.5e3 · 5! = 120'],
]

interface HistoryEntry {
	readonly expression: string
	readonly result: string
}

function preview(input: string, angle: AngleMode): Preview {
	if (input.trim() === '') return { kind: 'empty' }
	try {
		return { kind: 'result', text: format(evaluate(input, angle)) }
	} catch (error) {
		if (error instanceof EvalError) return { kind: 'error', message: evalMessage(error) }
		throw error
	}
}

/** Scientific calculator (Krackn `CalculatorTool`): free-form entry with a live result. */
export function CalculatorTool() {
	const [input, setInput] = useState('')
	const [angle, setAngle] = useState<AngleMode>(AngleMode.radians)
	const [history, setHistory] = useState<readonly HistoryEntry[]>([])
	const shown = useMemo(() => preview(input, angle), [input, angle])

	const commit = (): void => {
		if (shown.kind !== 'result') return
		setHistory((entries) => [{ expression: input.trim(), result: shown.text }, ...entries].slice(0, HISTORY_LIMIT))
	}

	return (
		<ToolPage subtitle="Type an expression. Functions, constants and factorials all work.">
			<div className="math-row">
				<PillField aria-label="Expression" placeholder="e.g. sin(pi/4) * sqrt(2)" spellCheck={false} value={input} onChange={setInput} onSubmit={commit} />
				<SegmentedControl options={angleOptions} value={angle} onChange={setAngle} aria-label="Angle unit" width={120} />
			</div>

			<div className="calc-line">
				{shown.kind === 'result' ? (
					<>
						<span className="calc-equals">=</span>
						<span className="calc-result math-mono" data-testid="calc-result">
							{shown.text}
						</span>
						<CopyButton value={shown.text} label="result" />
					</>
				) : null}
				{shown.kind === 'error' ? (
					<span className="calc-error" data-testid="calc-error" role="alert">
						{shown.message}
					</span>
				) : null}
				{shown.kind === 'empty' ? <span className="tool-subtitle">Press Return to keep a result.</span> : null}
			</div>

			<div className="math-row calc-keys">
				{keys.map((key) => (
					<PillButton key={key} variant="outlined" size="small" onClick={() => setInput((text) => text + key)}>
						{key}
					</PillButton>
				))}
				<span className="calc-spacer" />
				<PillButton variant="ghost" size="small" onClick={() => setInput('')}>
					Clear
				</PillButton>
			</div>

			{history.length > 0 ? (
				<SectionCard title="History" compact>
					{history.map((entry, index) => (
						<button key={index} type="button" className="calc-history-row" onClick={() => setInput(entry.expression)}>
							<span className="math-mono calc-history-expression">{entry.expression}</span>
							<span className="math-mono calc-history-result">{entry.result}</span>
						</button>
					))}
					<PillButton variant="ghost" size="small" onClick={() => setHistory([])}>
						Clear History
					</PillButton>
				</SectionCard>
			) : null}

			<SectionCard title="Supported" compact>
				{supported.map(([label, value]) => (
					<div key={label + value} className="calc-reference">
						<span className="calc-reference-label">{label}</span>
						<span className="math-mono">{value}</span>
					</div>
				))}
			</SectionCard>
		</ToolPage>
	)
}
