import { useState } from 'react'
import { decodeJWT } from '@shared/devtools/jwt'
import { InfoBanner } from '../../components'
import { CodeBlock, DevTool, ResultRow, TextArea } from './parts'

type Reading = { readonly token: string; readonly now: Date }

export function JwtTool() {
	// `now` is read when the token changes: it only decides whether `exp` has passed.
	const [{ token, now }, setReading] = useState<Reading>(() => ({ token: '', now: new Date() }))
	const empty = token.trim() === ''
	const parts = empty ? null : decodeJWT(token, { now })

	return (
		<DevTool>
			<TextArea label="JWT" value={token} onChange={(value) => setReading({ token: value, now: new Date() })} rows={5} />
			{empty ? <InfoBanner kind="info">Paste a token to inspect it. Decoding only: checking a signature needs the signing key.</InfoBanner> : null}
			{!empty && parts === null ? <InfoBanner kind="warning">That doesn't parse as a JWT (it needs header.payload.signature).</InfoBanner> : null}
			{parts?.expiry ? <InfoBanner kind={parts.expiry.includes('EXPIRED') ? 'warning' : 'info'}>Expires: {parts.expiry}</InfoBanner> : null}
			{parts ? (
				<>
					<CodeBlock label="Header" text={parts.header} />
					<CodeBlock label="Payload" text={parts.payload} />
					<ResultRow label="Signature" value={parts.signature} />
				</>
			) : null}
		</DevTool>
	)
}
