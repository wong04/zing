import { BadArgumentError, EvalError, evaluate, type AngleMode } from '@shared/math/expression'
import { evalMessage } from './evalMessage'

/**
 * What is wrong with a plot's expression, tried at x = 1; null when it is fine or empty. A curve may be undefined at
 * the probe (`sqrt(x-2)`), which isn't a mistake: sampling already skips the x values where it throws.
 */
export function plotProblem(text: string, angle: AngleMode): string | null {
	if (text.trim() === '') return null
	try {
		evaluate(text, angle, { x: 1 })
		return null
	} catch (error) {
		if (error instanceof BadArgumentError) return null
		if (error instanceof EvalError) return evalMessage(error)
		throw error
	}
}
