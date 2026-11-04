import { useState } from 'react'
import { cleanLink, InvalidLinkError, type CleanedLink } from '@shared/browser/linkCleaner'
import { InfoBanner, PillField } from '../../components'
import { ResultRow, ToolPage } from '../parts'

type Outcome = { readonly cleaned: CleanedLink } | { readonly invalid: string }

function clean(input: string): Outcome {
	try {
		return { cleaned: cleanLink(input) }
	} catch (error) {
		if (error instanceof InvalidLinkError) return { invalid: error.message }
		throw error
	}
}

export function LinkCleaner() {
	const [input, setInput] = useState('')
	const outcome = input.trim() === '' ? null : clean(input)
	return (
		<ToolPage subtitle="Strips tracking parameters so a link does not carry where you found it.">
			<PillField label="Link" value={input} onChange={setInput} placeholder="Paste a link" spellCheck={false} />
			{outcome && 'invalid' in outcome ? <InfoBanner kind="warning">{outcome.invalid}</InfoBanner> : null}
			{outcome && 'cleaned' in outcome ? (
				<>
					<ResultRow label="Clean link" value={outcome.cleaned.url} testId="cleaned-link" />
					<InfoBanner kind="info">
						{outcome.cleaned.removed.length === 0
							? 'No tracking parameters found: already clean.'
							: `Removed ${outcome.cleaned.removed.length}: ${outcome.cleaned.removed.join(', ')}`}
					</InfoBanner>
				</>
			) : null}
		</ToolPage>
	)
}
