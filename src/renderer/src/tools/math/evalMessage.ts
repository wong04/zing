import { EmptyExpressionError, type EvalError } from '@shared/math/expression'

/** What to tell the user about a failed evaluation; an empty message means the input just ended early. */
export function evalMessage(error: EvalError): string {
	return error instanceof EmptyExpressionError ? 'Incomplete expression' : error.message
}
