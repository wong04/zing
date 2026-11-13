# Builds Zing and packs it with Velopack into artifacts/releases (Setup.exe, the full package, releases.<channel>.json).
# `npm ci` must have run. ZingGoogleClientId and ZingGoogleClientSecret reach MSBuild from the environment when set; this script never reads or prints them.
param(
	[Parameter(Mandatory = $true)][string]$Version,
	[string]$Channel = 'win'
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Invoke-Step([string]$Name, [scriptblock]$Command) {
	Write-Host "==> $Name"
	& $Command
	if ($LASTEXITCODE -ne 0) {
		throw "$Name failed with exit code $LASTEXITCODE"
	}
}

$publish = 'artifacts/publish'
$releases = 'artifacts/releases'

Invoke-Step 'npm run build' { npm run build }
Invoke-Step 'npm run gen:reader' { npm run gen:reader }
Invoke-Step 'npm run gen:notices' { npm run gen:notices }

# A stale file from an earlier publish would ship in the package.
if (Test-Path $publish) {
	Remove-Item -Recurse -Force $publish
}
# ReadyToRun: the host's code (and the WinRT projections, which aren't precompiled) ships compiled, so start-up doesn't JIT
# it on the way to the first paint. About 300 ms off a relaunch's first paint, for about 54 MB more installed (15 MB in the package).
Invoke-Step 'dotnet publish' {
	dotnet publish host/Zing.Host -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true "-p:Version=$Version" -p:ZingRendererFolder=renderer -o $publish
}
Copy-Item -Recurse out/renderer "$publish/renderer"

Invoke-Step 'dotnet tool restore' { dotnet tool restore }
Invoke-Step 'vpk pack' {
	dotnet vpk pack --packId ZingBrowser --packVersion $Version --packDir $publish --mainExe Zing.exe --channel $Channel --runtime win-x64 `
		--packTitle Zing --packAuthors 'Aaden Wong' --icon build/icons/icon.ico --splashImage build/installer/splash.png `
		--framework webview2 --outputDir $releases
}
