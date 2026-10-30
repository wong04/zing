// External tools Convert and Download need, installed through winget instead
// of Krackn's Homebrew / direct-download paths (Core/ToolInstaller.swift).
// winget is part of Windows 11 and verifies publisher hashes, so Zing never
// downloads and runs an executable itself.
import { runProcess } from '../process'

export type ToolId = 'ffmpeg' | 'yt-dlp' | 'imagemagick' | 'pandoc' | 'uv'

export interface ToolSpec {
	id: ToolId
	title: string
	executable: string
	wingetId: string
	versionArgs: string[]
	purpose: string
}

export const TOOLS: Record<ToolId, ToolSpec> = {
	ffmpeg: { id: 'ffmpeg', title: 'FFmpeg', executable: 'ffmpeg', wingetId: 'Gyan.FFmpeg', versionArgs: ['-version'], purpose: 'Audio and video conversion' },
	'yt-dlp': { id: 'yt-dlp', title: 'yt-dlp', executable: 'yt-dlp', wingetId: 'yt-dlp.yt-dlp', versionArgs: ['--version'], purpose: 'Video and audio downloads' },
	imagemagick: { id: 'imagemagick', title: 'ImageMagick', executable: 'magick', wingetId: 'ImageMagick.ImageMagick', versionArgs: ['-version'], purpose: 'RAW, HEIC, SVG and other image formats' },
	pandoc: { id: 'pandoc', title: 'Pandoc', executable: 'pandoc', wingetId: 'JohnMacFarlane.Pandoc', versionArgs: ['--version'], purpose: 'Markdown, ePub, LaTeX and other document formats' },
	// meshio (3D/FEA meshes) runs through `uv tool run`, so uv is the only install.
	uv: { id: 'uv', title: 'uv', executable: 'uv', wingetId: 'astral-sh.uv', versionArgs: ['--version'], purpose: '3D and simulation mesh conversion (meshio)' },
}

// No --accept-*-agreements: winget runs in a visible terminal so the user
// sees and accepts the package's licence themselves.
export function wingetInstallArgs(tool: ToolSpec): string[] {
	return ['install', '--id', tool.wingetId, '--exact', '--source', 'winget']
}

// Krackn's lesson: a broken yt-dlp extractor is fixed by updating, and that
// update has to be one click away or nobody does it.
export function wingetUpgradeArgs(tool: ToolSpec): string[] {
	return ['upgrade', '--id', tool.wingetId, '--exact', '--source', 'winget']
}

// The first line of `tool --version` output, reduced to the version itself
// where it's recognisable ("ffmpeg version 7.1-full_build..." -> "7.1-full_build").
export function parseVersion(output: string): string | undefined {
	const first = output.split(/\r?\n/).find((l) => l.trim() !== '')?.trim()
	if (!first) return undefined
	const match = /version\s+(\S+)/i.exec(first) ?? /(\d+(?:\.\d+)+\S*)/.exec(first)
	return match?.[1] ?? first
}

export async function toolVersion(executablePath: string, tool: ToolSpec): Promise<string | undefined> {
	const result = await runProcess(executablePath, tool.versionArgs, { timeoutMs: 15_000 })
	if (result.exitCode !== 0) return undefined
	return parseVersion(result.stdout || result.stderr)
}
