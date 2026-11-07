import { useMemo, useState } from 'react'
import { HashAlgorithm, hashAlgorithms, isLegacy } from '@shared/devtools/hashAlgorithm'
import type { PickedFile } from '@shared/engines/engineJob'
import { InfoBanner, PillButton, SegmentedControl } from '../../components'
import { zing } from '../../api'
import { ResultRow, TextArea, ToolPage } from '../parts'
import { describeFailure } from './parts'
import { useTask } from './useTask'

type TextRequest = { readonly text: string; readonly algorithm: HashAlgorithm }
type FileRequest = { readonly file: PickedFile; readonly algorithm: HashAlgorithm }

const hashText = ({ text, algorithm }: TextRequest): Promise<string> => zing.hashText(text, algorithm)
const hashFile = ({ file, algorithm }: FileRequest): Promise<string> => zing.hashFile(file.token, algorithm)

const options = hashAlgorithms.map((value) => ({ value, label: value }))

export function HashTool() {
	const [algorithm, setAlgorithm] = useState<HashAlgorithm>(HashAlgorithm.sha256)
	const [text, setText] = useState('')
	const [fileRequest, setFileRequest] = useState<FileRequest | null>(null)
	const [pickError, setPickError] = useState<unknown>(null)
	const textRequest = useMemo(() => (text === '' ? null : { text, algorithm }), [text, algorithm])
	const textTask = useTask(textRequest, hashText)
	const fileTask = useTask(fileRequest, hashFile)
	// A file the person picks, never a typed path: the host opens only files it handed out a token for.
	const pickAFile = (): void => {
		setPickError(null)
		zing.pickFiles(['any']).then(
			(files) => {
				const [file] = files
				if (file) setFileRequest({ file, algorithm })
			},
			(error: unknown) => setPickError(error),
		)
	}

	return (
		<ToolPage>
			<SegmentedControl
				aria-label="Algorithm"
				options={options}
				value={algorithm}
				onChange={(next) => {
					setAlgorithm(next)
					setFileRequest(null)
				}}
			/>
			{isLegacy(algorithm) ? (
				<InfoBanner kind="info">{algorithm} is broken for security purposes. Fine for verifying a download, not for anything that needs to resist tampering.</InfoBanner>
			) : null}
			<TextArea label="Text to hash" value={text} onChange={setText} rows={5} />
			{textTask.kind === 'failed' ? <InfoBanner kind="warning">{describeFailure(textTask.error)}</InfoBanner> : null}
			<ResultRow label="Digest" value={textTask.kind === 'done' ? textTask.value : ''} />

			<div className="dev-row">
				<PillButton variant="outlined" size="small" onClick={pickAFile}>
					Hash a File
				</PillButton>
				{fileRequest ? <span className="tool-label">{fileRequest.file.name}</span> : null}
			</div>
			{pickError !== null ? <InfoBanner kind="warning">{describeFailure(pickError)}</InfoBanner> : null}
			{fileTask.kind === 'failed' ? <InfoBanner kind="warning">{describeFailure(fileTask.error)}</InfoBanner> : null}
			{fileTask.kind === 'running' ? <InfoBanner kind="info">Hashing…</InfoBanner> : null}
			{fileTask.kind === 'done' ? <ResultRow label="File digest" value={fileTask.value} /> : null}
		</ToolPage>
	)
}
