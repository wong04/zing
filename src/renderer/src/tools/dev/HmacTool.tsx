import { useMemo, useState } from 'react'
import { HashAlgorithm, hashAlgorithms } from '@shared/devtools/hashAlgorithm'
import { InfoBanner, PillField, SegmentedControl } from '../../components'
import { zing } from '../../api'
import { ResultRow, TextArea, ToolPage } from '../parts'
import { describeFailure } from './parts'
import { useTask } from './useTask'

type Request = { readonly message: string; readonly key: string; readonly algorithm: HashAlgorithm }

const sign = ({ message, key, algorithm }: Request): Promise<string> => zing.hmac(message, key, algorithm)

const options = hashAlgorithms.map((value) => ({ value, label: value }))

export function HmacTool() {
	const [algorithm, setAlgorithm] = useState<HashAlgorithm>(HashAlgorithm.sha256)
	const [key, setKey] = useState('')
	const [message, setMessage] = useState('')
	const request = useMemo(() => (message === '' && key === '' ? null : { message, key, algorithm }), [message, key, algorithm])
	const task = useTask(request, sign)

	return (
		<ToolPage>
			<SegmentedControl aria-label="Algorithm" options={options} value={algorithm} onChange={setAlgorithm} />
			<PillField aria-label="Secret key" value={key} onChange={setKey} placeholder="Secret key" spellCheck={false} />
			<TextArea label="Message" value={message} onChange={setMessage} rows={5} />
			{task.kind === 'failed' ? <InfoBanner kind="warning">{describeFailure(task.error)}</InfoBanner> : null}
			<ResultRow label={`HMAC-${algorithm}`} value={task.kind === 'done' ? task.value : ''} />
		</ToolPage>
	)
}
