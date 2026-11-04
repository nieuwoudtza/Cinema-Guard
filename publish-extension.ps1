#requires -Version 5.1
<#
    Packs the browser extension for the Chrome Web Store: deploy\chrome-web-store\cinema-guard-<version>.zip.

    The zip lands beside the store listing, its description and images, so the folder holds everything the developer
    dashboard asks for. Older zips are removed: only the version being uploaded stays.

    The version comes from extension\manifest.json; raise it there before each upload (the store refuses a version it
    already has). The zip holds the extension folder without its README. Entries are written with forward slashes:
    Windows PowerShell's Compress-Archive writes backslashes, which the store rejects.

    Example:
      .\publish-extension.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$source = Join-Path $PSScriptRoot 'extension'
$manifest = Get-Content (Join-Path $source 'manifest.json') -Raw | ConvertFrom-Json
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
