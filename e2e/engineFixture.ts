import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const FAKE_ENGINE_PROJECT = join(import.meta.dirname, '..', 'host', 'Zing.FakeEngine')

/** What each engine is called in the fixture: a single file, so nothing needs unpacking. */
const FILES: Readonly<Record<string, { readonly file: string; readonly executable: string }>> = {
	ffmpeg: { file: 'ffmpeg.exe', executable: 'ffmpeg.exe' },
	magick: { file: 'magick.exe', executable: 'magick.exe' },
	pandoc: { file: 'pandoc.exe', executable: 'pandoc.exe' },
	ytdlp: { file: 'yt-dlp.exe', executable: 'yt-dlp.exe' },
	bgmodel: { file: 'isnet-general-use.onnx', executable: 'isnet-general-use.onnx' },
}

export interface EngineFixture {
	/** For `launchZing({ env })`: points the engine catalog at this server. */
	readonly env: Record<string, string>
	/** The path of every request the server has answered, in order. */
	readonly requests: string[]
	/** The size of each engine's download. */
	readonly bytes: number
	close(): Promise<void>
}

/**
 * A local release host that serves Zing.FakeEngine in place of FFmpeg and the rest, so a test downloads
 * something real without the internet. `missing` ids answer 404.
 */
export async function startEngineFixture(options: { readonly missing?: readonly string[] } = {}): Promise<EngineFixture> {
	const dir = mkdtempSync(join(tmpdir(), 'zing-engine-fixture-'))
	execFileSync('dotnet', ['publish', FAKE_ENGINE_PROJECT, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true', '-o', dir], { stdio: 'ignore' })
	const body = readFileSync(join(dir, 'Zing.FakeEngine.exe'))
	const sha256 = createHash('sha256').update(body).digest('hex')
	const requests: string[] = []
	const missing = new Set((options.missing ?? []).map((id) => `/${FILES[id]!.file}`))
	const server: Server = createServer((req, res) => {
		requests.push(req.url ?? '')
		if (missing.has(req.url ?? '') || !Object.values(FILES).some((f) => `/${f.file}` === req.url)) {
			res.statusCode = 404
			res.end()
			return
		}
		res.setHeader('content-length', body.length)
		res.end(body)
	})
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
	const manifest = join(dir, 'catalog.json')
	const entries = Object.entries(FILES).map(([id, f]) => ({ id, version: '1.0', file: f.file, sha256, bytes: body.length, executable: f.executable }))
	writeFileSync(manifest, JSON.stringify(entries))
	return {
		env: { ZING_ENGINE_BASE_URL: `http://127.0.0.1:${(server.address() as AddressInfo).port}/`, ZING_ENGINE_CATALOG: manifest },
		requests,
		bytes: body.length,
		close: () =>
			new Promise<void>((resolve) => {
				server.close(() => {
					rmSync(dir, { recursive: true, force: true })
					resolve()
				})
			}),
	}
}
