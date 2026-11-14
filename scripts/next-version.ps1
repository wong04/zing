# Prints the version after the highest ZingBrowser full package in a Velopack releases folder: its patch, plus one.
param(
	[Parameter(Mandatory = $true)][string]$Releases
)

$ErrorActionPreference = 'Stop'

$versions = Get-ChildItem -Path $Releases -Filter 'ZingBrowser-*-full.nupkg' -File |
	ForEach-Object { if ($_.Name -match '^ZingBrowser-(\d+\.\d+\.\d+)-full\.nupkg$') { [version]$Matches[1] } }
$highest = $versions | Sort-Object -Descending | Select-Object -First 1
if ($null -eq $highest) {
	[Console]::Error.WriteLine("No ZingBrowser release in $Releases to follow. Pack the first one with: npm run pack -- -Version 0.9.2")
	exit 1
}
Write-Output "$($highest.Major).$($highest.Minor).$($highest.Build + 1)"
