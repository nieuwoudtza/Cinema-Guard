#requires -Version 5.1
<#
    Packs the browser extension for the Chrome Web Store: deploy\chrome-web-store\cinema-guard-<version>.zip.

    The zip lands beside the store listing, its description and images, so the folder holds everything the developer
    dashboard asks for. Older zips are removed: only the version being uploaded stays.

    Each run raises the last part of the version in extension\manifest.json (1.0.1 becomes 1.0.2) before packing, since
    the store refuses a version it already has, and names the "## Unreleased" heading in extension\CHANGELOG.md after
    it. The manifest is edited as text, so its layout stays as written. The zip
    holds the extension folder without its README. Entries are written with forward slashes: Windows PowerShell's
    Compress-Archive writes backslashes, which the store rejects.

    Example:
      .\publish-extension.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$source = Join-Path $PSScriptRoot 'extension'
$manifestPath = Join-Path $source 'manifest.json'
$manifestText = [System.IO.File]::ReadAllText($manifestPath)
$versionPattern = '("version"\s*:\s*")([0-9.]+)(")'
$versionMatch = [regex]::Match($manifestText, $versionPattern)
if (-not $versionMatch.Success) { throw "No version found in $manifestPath" }

$parts = @($versionMatch.Groups[2].Value.Split('.') | ForEach-Object { [int]$_ })
$parts[-1]++
if ($parts[-1] -gt 65535) { throw "Version part $($parts[-1]) is over Chrome's limit of 65535; raise an earlier part by hand." }
$version = $parts -join '.'

$manifestText = [regex]::new($versionPattern).Replace($manifestText, "`${1}$version`${3}", 1)
[System.IO.File]::WriteAllText($manifestPath, $manifestText, (New-Object System.Text.UTF8Encoding $false))
$manifest = $manifestText | ConvertFrom-Json

# The changes under "## Unreleased" become this version's; the options page and the popup show them. The dash is
# written as a code point because Windows PowerShell reads this file as ANSI.
$changelogPath = Join-Path $source 'CHANGELOG.md'
$changelog = [System.IO.File]::ReadAllText($changelogPath)
$unreleased = [regex]::Match($changelog, '(?m)^## Unreleased[^\r\n]*')
if ($unreleased.Success) {
    $heading = '## {0} {1} {2}' -f $version, [char]0x2014, (Get-Date).ToString('d MMMM yyyy', [Globalization.CultureInfo]::InvariantCulture)
    $changelog = $changelog.Substring(0, $unreleased.Index) + $heading + $changelog.Substring($unreleased.Index + $unreleased.Length)
    [System.IO.File]::WriteAllText($changelogPath, $changelog, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "CHANGELOG.md: $heading"
}
else {
    Write-Warning "extension\CHANGELOG.md has no '## Unreleased' section, so What's new lists no changes for $version."
}
$outputFolder = Join-Path $PSScriptRoot 'deploy\chrome-web-store'
$zipPath = Join-Path $outputFolder "cinema-guard-$($manifest.version).zip"

New-Item -ItemType Directory -Force -Path $outputFolder | Out-Null
Get-ChildItem $outputFolder -Filter 'cinema-guard-*.zip' | Remove-Item

$excluded = @('README.md')
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $source -Recurse -File | Where-Object { $excluded -notcontains $_.Name } | ForEach-Object {
        $entry = $_.FullName.Substring($source.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

$size = [math]::Round((Get-Item $zipPath).Length / 1KB)
Write-Host "Cinema Guard $($manifest.version) packed: $zipPath ($size KB)"
