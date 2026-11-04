import { useMemo, useState } from 'react'
import { HashAlgorithm, hashAlgorithms, isLegacy } from '@shared/devtools/hashAlgorithm'
import { InfoBanner, PillButton, PillField, SegmentedControl } from '../../components'
import { fileName } from '../../components/filePath'
import { zing } from '../../api'
import { ResultRow, TextArea, ToolPage } from '../parts'
import { describeFailure } from './parts'
import { useTask } from './useTask'

type TextRequest = { readonly text: string; readonly algorithm: HashAlgorithm }
type FileRequest = { readonly path: string; readonly algorithm: HashAlgorithm }

const hashText = ({ text, algorithm }: TextRequest): Promise<string> => zing.hashText(text, algorithm)
const hashFile = ({ path, algorithm }: FileRequest): Promise<string> => zing.hashFile(path, algorithm)

const options = hashAlgorithms.map((value) => ({ value, label: value }))

export function HashTool() {
	const [algorithm, setAlgorithm] = useState<HashAlgorithm>(HashAlgorithm.sha256)
	const [text, setText] = useState('')
	const [path, setPath] = useState('')
	const [fileRequest, setFileRequest] = useState<FileRequest | null>(null)
	const textRequest = useMemo(() => (text === '' ? null : { text, algorithm }), [text, algorithm])
	const textTask = useTask(textRequest, hashText)
	const fileTask = useTask(fileRequest, hashFile)
	const hashThePath = (): void => {
		if (path !== '') setFileRequest({ path, algorithm })
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

			<PillField aria-label="File path" value={path} onChange={setPath} onSubmit={hashThePath} placeholder={String.raw`C:\Users\you\Downloads\file.iso`} spellCheck={false} />
			<div className="dev-row">
				<PillButton variant="outlined" size="small" disabled={path === ''} onClick={hashThePath}>
					Hash file
				</PillButton>
				{fileRequest ? <span className="tool-label">{fileName(fileRequest.path)}</span> : null}
			</div>
			{fileTask.kind === 'failed' ? <InfoBanner kind="warning">{describeFailure(fileTask.error)}</InfoBanner> : null}
			{fileTask.kind === 'running' ? <InfoBanner kind="info">Hashing…</InfoBanner> : null}
			{fileTask.kind === 'done' ? <ResultRow label="File digest" value={fileTask.value} /> : null}
		</ToolPage>
	)
}
