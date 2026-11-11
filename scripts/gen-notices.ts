// Writes the third-party notices next to the host's other assets: the production npm packages (what the renderer
// bundles) and the NuGet packages Zing.Host restores, each with its licence text. A package with none fails the run.
import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { formatNotices, overrideFile, type NoticePackage } from './notices'

const root = join(import.meta.dirname, '..')
const output = join(root, 'host', 'Zing.Host', 'Assets', 'THIRD-PARTY-NOTICES.txt')
const LICENSE_FILE = /^licen[cs]e([-.].*)?$/i

function run(command: string, args: readonly string[], allowFailure = false): string {
	const result = spawnSync(command, args, { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })
	if (result.error) throw result.error
	if (result.status !== 0 && !allowFailure) throw new Error(`${command} ${args.join(' ')} failed (${result.status}): ${result.stderr}`)
	return result.stdout
}

/** npm's own script, which `npm run` names; spawning npm.cmd directly would need a shell. */
function npmCli(): string {
	const cli = process.env.npm_execpath
	if (!cli) throw new Error('Run this through `npm run gen:notices`, which tells it where npm is')
	return cli
}

/** Every file in the folder that looks like a licence, joined; empty when there is none. */
function licenseFiles(folder: string): string {
	return readdirSync(folder)
		.filter((file) => LICENSE_FILE.test(file))
		.sort()
		.map((file) => readFileSync(join(folder, file), 'utf8').trim())
		.join('\n\n')
}

/** The package's own licence file, else the text committed in scripts/licenses for a package that ships none. */
function npmLicenseText(name: string, path: string): string {
	const text = licenseFiles(path)
	if (text) return text
	const override = overrideFile(name)
	if (!existsSync(override)) throw new Error(`No licence found for ${name}: it ships none and ${override} does not exist`)
	return readFileSync(override, 'utf8').trim()
}

interface NpmNode {
	readonly version?: string
	readonly path?: string
	readonly license?: string
	readonly dependencies?: Record<string, NpmNode>
}

function npmPackages(): NoticePackage[] {
	// npm exits non-zero for problems it merely reports, such as optional platform binaries that are not installed.
	const tree = JSON.parse(run(process.execPath, [npmCli(), 'ls', '--omit=dev', '--all', '--json', '--long'], true)) as NpmNode
	const found = new Map<string, NoticePackage>()
	const visit = (dependencies: Record<string, NpmNode> | undefined): void => {
		for (const [name, node] of Object.entries(dependencies ?? {})) {
			// No path: an optional package for another platform, which this install doesn't have.
			if (node.path && node.version) {
				found.set(`${name}@${node.version}`, { name, version: node.version, license: node.license ?? 'see text', licenseText: npmLicenseText(name, node.path), source: 'npm' })
			}
			visit(node.dependencies)
		}
	}
	visit(tree.dependencies)
	return [...found.values()]
}

interface DotnetList {
	readonly projects: { readonly frameworks?: { readonly topLevelPackages?: NugetRef[]; readonly transitivePackages?: NugetRef[] }[] }[]
}
interface NugetRef {
	readonly id: string
	readonly resolvedVersion: string
}

function nugetPackage({ id, resolvedVersion: version }: NugetRef, packages: string): NoticePackage {
	const folder = join(packages, id.toLowerCase(), version)
	const nuspec = readFileSync(join(folder, `${id.toLowerCase()}.nuspec`), 'utf8')
	const [, kind, value] = /<license type="(\w+)">([^<]*)<\/license>/.exec(nuspec) ?? []
	const declaredFile = kind === 'file' && value ? join(folder, value) : undefined
	const expression = kind === 'expression' ? value : undefined
	const url = /<licenseUrl>([^<]*)<\/licenseUrl>/.exec(nuspec)?.[1]
	const fromFile = declaredFile && existsSync(declaredFile) ? readFileSync(declaredFile, 'utf8').trim() : licenseFiles(folder)
	// aka.ms/deprecateLicenseUrl is NuGet's placeholder for "see the licence file", which is no licence.
	const fromUrl = url && !url.includes('deprecateLicenseUrl') ? `License: ${url}` : ''
	const fromExpression = expression ? `License expression: ${expression}` : ''
	return { name: id, version, license: expression ?? 'see text', licenseText: fromFile || fromExpression || fromUrl, source: 'nuget' }
}

function nugetPackages(): NoticePackage[] {
	// Listing needs the restored assets file; restoring is also what the build does first.
	run('dotnet', ['restore', 'host/Zing.Host'])
	const list = JSON.parse(run('dotnet', ['list', 'host/Zing.Host', 'package', '--include-transitive', '--format', 'json'])) as DotnetList
	const packages = /global-packages:\s*(.+)/.exec(run('dotnet', ['nuget', 'locals', 'global-packages', '--list']))?.[1]?.trim()
	if (!packages) throw new Error('dotnet did not name the NuGet global packages folder')
	const refs = new Map<string, NugetRef>()
	for (const project of list.projects) {
		for (const framework of project.frameworks ?? []) {
			for (const ref of [...(framework.topLevelPackages ?? []), ...(framework.transitivePackages ?? [])]) refs.set(`${ref.id}@${ref.resolvedVersion}`, ref)
		}
	}
	return [...refs.values()].map((ref) => nugetPackage(ref, packages))
}

mkdirSync(join(root, 'host', 'Zing.Host', 'Assets'), { recursive: true })
writeFileSync(output, formatNotices([...npmPackages(), ...nugetPackages()]))
console.info('wrote host/Zing.Host/Assets/THIRD-PARTY-NOTICES.txt')
