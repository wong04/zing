# A hand-check build of the latest main, packed one patch above the last one so an installed Zing updates to it in place
# (Settings > About > Check now, with ZING_UPDATE_FEED set to artifacts\releases). See docs/release.md, "Trying a new build".
# Run it in a checkout kept for hand checks: it moves that checkout to origin/main.

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Invoke-Step([string]$Name, [scriptblock]$Command) {
	Write-Host "==> $Name"
	& $Command
	if ($LASTEXITCODE -ne 0) {
		throw "$Name failed with exit code $LASTEXITCODE"
	}
}

if (git status --porcelain --untracked-files=no) {
	throw 'This checkout has local changes. A hand-check checkout builds main as it is: commit or discard them first.'
}

Invoke-Step 'git fetch origin' { git fetch origin }
Invoke-Step 'git checkout origin/main' { git checkout --detach origin/main }
Invoke-Step 'npm ci' { npm ci }

# Google sign-in is built in only when both are set; asked for here, never written anywhere.
if (-not $env:ZingGoogleClientId) {
	$env:ZingGoogleClientId = Read-Host 'Google client ID'
}
if (-not $env:ZingGoogleClientSecret) {
	$env:ZingGoogleClientSecret = [Net.NetworkCredential]::new('', (Read-Host 'Google client secret' -AsSecureString)).Password
}

$releases = Join-Path (Get-Location) 'artifacts\releases'
$version = & (Join-Path $PSScriptRoot 'next-version.ps1') -Releases $releases
if ($LASTEXITCODE -ne 0) {
	throw 'Could not work out the next version.'
}

Write-Host "==> pack $version"
& (Join-Path $PSScriptRoot 'pack.ps1') -Version $version

Write-Host ''
Write-Host "Zing $version is in $releases."
Write-Host 'In Zing: Settings > About > Check now, then click "Relaunch to update".'
