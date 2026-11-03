import { EmptyExpressionError, EvalError, UnbalancedParenthesesError } from '@shared/math/expression'

/**
 * What to tell the user about a failed evaluation. Running out of input inside an
 * open bracket ("sin(") surfaces from the parser as an empty expression, which has no
 * message, so it is reported as the unclosed bracket it is.
 */
export function evalMessage(error: EvalError, input: string): string {
	if (!(error instanceof EmptyExpressionError)) return error.message
	const opened = input.split('(').length - 1
	const closed = input.split(')').length - 1
	return opened > closed ? new UnbalancedParenthesesError().message : 'Incomplete expression'
}
