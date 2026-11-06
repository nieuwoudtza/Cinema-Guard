#requires -Version 5.1
<#
    Builds a Cinema Guard release for the WaveBit update server.

    One command does everything:
      1. dotnet publish (self-contained x64, Windows App SDK bundled)
      2. checks the files WinUI and the speech engines need are really in the publish folder
      3. downloads the previous release from the feed, so Velopack can make small delta updates
      4. vpk pack: Velopack's setup and the packages (no portable zip)
      5. removes every older package, so the release folder holds this version only
      6. builds CinemaGuard-Setup.exe around Velopack's setup (folder choice, desktop shortcut)
      7. with -IisWebConfig only: copies web.config into the release folder
      8. removes everything the server does not need, so only these are left to upload:
           releases.win.json, the full and delta .nupkg, CinemaGuard-Setup.exe

    Then replace the contents of the IIS folder behind the feed URL with the release folder, deploy\desktop\feed.
    The publish folder, deploy\desktop\publish, is only the input to vpk pack.

    Before publishing, the "## Unreleased" heading in CHANGELOG.md becomes "## <version> - <date>" (with an em dash), so the app
    shows those changes after the update. Commit CHANGELOG.md with the release.

    Versions are dated: yyyy.MM.dd.<iteration>, where the iteration counts the day's releases: the first
    release of a day is 2026.11.01.1, the next 2026.11.01.2, ..., starting at 1 again the next day. The
    releases already made today are read from the feed, the local release folder and
    -PreviousReleasePath, so the count carries on across machines. Development builds keep their own
    count in build-number.json (Directory.Build.targets) and never move a release number. Normally no
    version is passed at all.

    Velopack only accepts 3-part SemVer versions, so the package itself is numbered year.MMdd.iteration
    (2026.11.01.1 -> 2026.1101.1; 5 January -> 2026.105.1). It sorts by day, then by iteration. The app,
    the setup window and the "update ready" bar show the dated form; the .nupkg file names, and the
    version Windows lists under Installed apps, show the package form.

    Examples:
      .\publish.ps1
      .\publish.ps1 -SkipPreviousDownload            (the very first release)
      .\publish.ps1 2026.11.01.3                     (force a number; must be newer than every release)
#>
[CmdletBinding()]
param(
    # Optional: yyyy.MM.dd.<iteration>. Without it: today, next free iteration.
    [Parameter(Position = 0)]
    [string]$Version = "",

    [string]$FeedUrl = "https://wavebit.dev/software/cinemaguard",

    [string]$PublishDir = "",
    [string]$ReleasesDir = "",

    # A .ico for the app, the setup file and the shortcuts. Defaults to Assets\AppIcon.ico when present.
    [string]$Icon = "",

    # The first release has no previous one to download.
    [switch]$SkipPreviousDownload,

    # Optional: a local folder holding the previous release (for a staging or offline build).
    [string]$PreviousReleasePath = "",

    # Skip the small custom setup window and ship Velopack's own setup only.
    [switch]$NoInstaller,

    # Add deploy\desktop\web.config: only for a standalone IIS site or virtual directory. The wavebit.dev MVC
    # app serves the folder with UseStaticFiles, where a web.config would just be a public file.
    [switch]$IisWebConfig
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $root "deploy\desktop\publish" }
if (-not $ReleasesDir) { $ReleasesDir = Join-Path $root "deploy\desktop\feed" }
if (-not $Icon) {
    $candidate = Join-Path $root "Cinema Guard\Assets\AppIcon.ico"
    if (Test-Path $candidate) { $Icon = $candidate }
}

function Step([string]$text) {
    Write-Host ""
    Write-Host "== $text" -ForegroundColor Cyan
}

function Fail([string]$text) {
    throw "publish failed: $text"
}

# ---------------------------------------------------------------- versions and tools

function PackageVersionOf([datetime]$date, [int]$iteration) {
    "{0}.{1}.{2}" -f $date.Year, ($date.Month * 100 + $date.Day), $iteration
}

# Every package version already released: the live feed, the local release folder, and the
# -PreviousReleasePath folder. The feed being unreachable is only a warning (the site may not be up yet).
function Get-ReleasedVersions {
    $found = New-Object System.Collections.Generic.List[string]

    try {
        $feed = Invoke-RestMethod -Uri ($FeedUrl.TrimEnd("/") + "/releases.win.json") -TimeoutSec 20 -Headers @{ "Cache-Control" = "no-cache" }
        foreach ($asset in @($feed.Assets)) { if ($asset.Version) { $found.Add([string]$asset.Version) } }
    }
    catch {
        Write-Warning "Could not read the versions on $FeedUrl ($($_.Exception.Message)). Only local releases are checked."
    }

    foreach ($folder in @($ReleasesDir, $PreviousReleasePath)) {
        if (-not $folder -or -not (Test-Path $folder)) { continue }

        $index = Join-Path $folder "releases.win.json"
        if (Test-Path $index) {
            $local = Get-Content $index -Raw | ConvertFrom-Json
            foreach ($asset in @($local.Assets)) { if ($asset.Version) { $found.Add([string]$asset.Version) } }
        }

        foreach ($package in Get-ChildItem $folder -Filter "WaveBit.CinemaGuard-*-full.nupkg" -ErrorAction SilentlyContinue) {
            if ($package.Name -match '^WaveBit\.CinemaGuard-(.+)-full\.nupkg$') { $found.Add($Matches[1]) }
        }
    }

    $found | Sort-Object -Unique
}

$released = @(Get-ReleasedVersions)

if ($Version) {
    if ($Version -notmatch '^(\d{4}\.\d{2}\.\d{2})\.(\d+)$') {
        Fail "the version must look like 2026.11.01.1 (yyyy.MM.dd.iteration)."
    }

    $date = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($Matches[1], "yyyy.MM.dd", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date)) {
        Fail "$($Matches[1]) is not a real date."
    }

    $iteration = [int]$Matches[2]
    if ($iteration -lt 1 -or $iteration -gt 65535) { Fail "the iteration must be between 1 and 65535." }
}
else {
    $date = (Get-Date).Date

    # The day's first release is 1; after that, one past the highest release made today.
    $iteration = 1
    $prefix = "{0}.{1}." -f $date.Year, ($date.Month * 100 + $date.Day)
    $pattern = "^" + [regex]::Escape($prefix) + "(\d+)$"
    $today = @($released | Where-Object { $_ -match $pattern } | ForEach-Object { [int]($_ -replace $pattern, '$1') })
    if ($today.Count -gt 0) {
        $iteration = ($today | Measure-Object -Maximum).Maximum + 1
    }
}

$PackageVersion = PackageVersionOf $date $iteration
$DisplayVersion = "{0}.{1}" -f $date.ToString("yyyy.MM.dd"), $iteration
# Windows file properties need four numbers, each at most 65535.
$FileVersion = "{0}.{1}.{2}.{3}" -f $date.Year, $date.Month, $date.Day, $iteration

if ($released -contains $PackageVersion) {
    Fail "$DisplayVersion ($PackageVersion) has already been released. Leave the version out to get the next one."
}

# Clients only move to a higher version: a number below the newest release would never install.
$newest = $released | ForEach-Object { $v = $null; if ([version]::TryParse($_, [ref]$v)) { $v } } | Sort-Object -Descending | Select-Object -First 1
if ($newest -and [version]$PackageVersion -le $newest) {
    Fail "$DisplayVersion ($PackageVersion) is not newer than the latest release $newest, so no one would get it."
}

Write-Host "Version $DisplayVersion (package $PackageVersion)" -ForegroundColor Green

# Only a copy running from the publish folder locks it; the installed app (under Programs) does not.
$publishPrefix = [IO.Path]::GetFullPath($PublishDir).TrimEnd("\") + "\"
$running = Get-Process -Name "Cinema Guard" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Fail "Cinema Guard is running from $PublishDir. Close it first so the publish folder can be replaced."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail "dotnet is not on PATH." }
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "vpk is not installed; installing it as a global dotnet tool..." -ForegroundColor Yellow
    & dotnet tool install --global vpk
    if ($LASTEXITCODE -ne 0) { Fail "could not install vpk (dotnet tool install --global vpk)." }
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}

# ---------------------------------------------------------------- changelog

# The changes under "## Unreleased" become this release's: the app embeds CHANGELOG.md and shows them after the
# update. A run that failed after this step left its version on top without releasing it; that heading is renamed
# instead. The dash is written as a code point because Windows PowerShell reads this file as ANSI.
$changelogPath = Join-Path $root "CHANGELOG.md"
$changelog = [IO.File]::ReadAllText($changelogPath)
$heading = "## {0} {1} {2}" -f $DisplayVersion, [char]0x2014, $date.ToString("d MMMM yyyy", [Globalization.CultureInfo]::InvariantCulture)
$top = [regex]::Match($changelog, '(?m)^## (\S+)[^\r\n]*')
$topPackage = $null
if ($top.Success -and $top.Groups[1].Value -match '^(\d{4})\.(\d{2})\.(\d{2})\.(\d+)$') {
    $topPackage = "{0}.{1}.{2}" -f [int]$Matches[1], ([int]$Matches[2] * 100 + [int]$Matches[3]), [int]$Matches[4]
}

if ($top.Success -and ($top.Groups[1].Value -eq "Unreleased" -or ($topPackage -and $released -notcontains $topPackage))) {
    $changelog = $changelog.Substring(0, $top.Index) + $heading + $changelog.Substring($top.Index + $top.Length)
    [IO.File]::WriteAllText($changelogPath, $changelog, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "CHANGELOG.md: $heading" -ForegroundColor Green
}
else {
    Write-Warning "CHANGELOG.md has no '## Unreleased' section, so the app shows no changes for $DisplayVersion."
}

# CgReleaseBuild: this version is final, so Directory.Build.targets neither stamps nor counts the build.
$versionArgs = @("-p:Version=$PackageVersion", "-p:InformationalVersion=$DisplayVersion", "-p:FileVersion=$FileVersion", "-p:CgReleaseBuild=true")

# ---------------------------------------------------------------- publish

Step "Publishing $DisplayVersion to $PublishDir"

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $ReleasesDir | Out-Null

& dotnet publish (Join-Path $root "Cinema Guard\Cinema Guard.csproj") `
    -c Release `
    -p:Platform=x64 `
    -p:PublishProfile=win-x64 `
    @versionArgs `
    -o $PublishDir `
    --nologo

if ($LASTEXITCODE -ne 0) { Fail "dotnet publish returned $LASTEXITCODE." }

Step "Checking the publish folder"

$required = @(
    "Cinema Guard.exe",
    "Cinema Guard.pri",
    "CinemaGuard.Core.dll",
    "Whisper.net.dll",
    "runtimes\win-x64\whisper.dll",
    "runtimes\noavx\win-x64\whisper.dll",
    "sherpa-onnx-c-api.dll",
    "onnxruntime.dll"
)

$missing = @()
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $PublishDir $file))) { $missing += $file }
}

$xbf = Get-ChildItem $PublishDir -Recurse -Filter *.xbf -ErrorAction SilentlyContinue
if (-not $xbf -or $xbf.Count -eq 0) { $missing += "the app's compiled XAML (*.xbf)" }

if ($missing.Count -gt 0) {
    Fail ("these files are missing from the publish folder: " + ($missing -join ", "))
}

$size = (Get-ChildItem $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("OK: {0:N1} MB, {1} files" -f ($size / 1MB), (Get-ChildItem $PublishDir -Recurse -File).Count) -ForegroundColor Green

# ---------------------------------------------------------------- pack

Step "Fetching the previous release for delta updates"

$deltaArgs = @()
if ($SkipPreviousDownload) {
    Write-Host "Skipped (-SkipPreviousDownload)." -ForegroundColor Yellow
    if (-not (Test-Path (Join-Path $ReleasesDir "releases.win.json"))) {
        $deltaArgs = @("--delta", "None")
    }
}
else {
    try {
        if ($PreviousReleasePath) {
            & vpk download local --path $PreviousReleasePath --outputDir $ReleasesDir
        }
        else {
            & vpk download http --url $FeedUrl --outputDir $ReleasesDir --allowEmptyChannel
        }

        if ($LASTEXITCODE -ne 0) { throw "vpk download returned $LASTEXITCODE." }
    }
    catch {
        if (Test-Path (Join-Path $ReleasesDir "releases.win.json")) {
            Write-Warning "Could not fetch the previous release ($($_.Exception.Message)). Using the packages already in the release folder."
        }
        else {
            Write-Warning "Could not reach $FeedUrl ($($_.Exception.Message)). This will be a full package."
            $deltaArgs = @("--delta", "None")
        }
    }
}

Step "Packing the release"

$packArgs = @(
    "pack",
    "--packId", "WaveBit.CinemaGuard",
    "--packVersion", $PackageVersion,
    "--packDir", $PublishDir,
    "--mainExe", "Cinema Guard.exe",
    "--packTitle", "Cinema Guard",
    "--packAuthors", "WaveBit",
    "--outputDir", $ReleasesDir,
    "--runtime", "win-x64",
    "--noPortable"
)
if ($Icon -and (Test-Path $Icon)) { $packArgs += @("--icon", (Resolve-Path $Icon).Path) }
$packArgs += $deltaArgs

& vpk @packArgs
if ($LASTEXITCODE -ne 0) { Fail "vpk pack returned $LASTEXITCODE." }

$velopackSetup = Join-Path $ReleasesDir "WaveBit.CinemaGuard-win-Setup.exe"
if (-not (Test-Path $velopackSetup)) { Fail "Velopack did not create WaveBit.CinemaGuard-win-Setup.exe." }

# ---------------------------------------------------------------- prune

Step "Keeping only $DisplayVersion in the release folder"

# Only this version's full package and its delta stay: a client one release behind takes the delta,
# any older one the full package. The indexes are rewritten too, so they list no file that is gone.
$keep = @("WaveBit.CinemaGuard-$PackageVersion-full.nupkg", "WaveBit.CinemaGuard-$PackageVersion-delta.nupkg")
$stale = @(Get-ChildItem $ReleasesDir -File | Where-Object {
    ($_.Name -like "*.nupkg" -and $keep -notcontains $_.Name) -or $_.Name -like "*.incomplete"
})
foreach ($file in $stale) {
    Remove-Item $file.FullName -Force
    Write-Host "Removed $($file.Name)"
}

$index = Join-Path $ReleasesDir "releases.win.json"
$assets = @((Get-Content $index -Raw | ConvertFrom-Json).Assets | Where-Object { $_.Version -eq $PackageVersion })
if (-not ($assets | Where-Object { $_.Type -eq "Full" })) { Fail "releases.win.json does not list the $PackageVersion full package." }
[IO.File]::WriteAllText($index, (ConvertTo-Json -InputObject @{ Assets = $assets } -Depth 5 -Compress))

# ---------------------------------------------------------------- installer

if (-not $NoInstaller) {
    Step "Building CinemaGuard-Setup.exe"

    $installerProject = Join-Path $root "installer\CinemaGuard.Installer\CinemaGuard.Installer.csproj"
    $setupPath = (Resolve-Path $velopackSetup).Path

    & dotnet build $installerProject -c Release --nologo `
        "-p:PayloadPath=$setupPath" `
        @versionArgs

    if ($LASTEXITCODE -ne 0) { Fail "building the installer returned $LASTEXITCODE." }

    $built = Join-Path $root "installer\CinemaGuard.Installer\bin\Release\net48\CinemaGuard-Setup.exe"
    if (-not (Test-Path $built)) { Fail "the installer build produced no CinemaGuard-Setup.exe." }

    Copy-Item $built (Join-Path $ReleasesDir "CinemaGuard-Setup.exe") -Force
}

# ---------------------------------------------------------------- IIS

$releaseWebConfig = Join-Path $ReleasesDir "web.config"
if ($IisWebConfig) {
    Step "Adding the IIS web.config"

    $webConfig = Join-Path $root "deploy\desktop\web.config"
    if (-not (Test-Path $webConfig)) { Fail "deploy\desktop\web.config is missing." }
    Copy-Item $webConfig $releaseWebConfig -Force
}
elseif (Test-Path $releaseWebConfig) {
    # Left by an earlier build; it must not be uploaded to the MVC-served folder.
    Remove-Item $releaseWebConfig -Force
}

# ---------------------------------------------------------------- upload set

Step "Removing what the server does not need"

# Clients read releases.win.json only: RELEASES is for apps moving over from Squirrel, which Cinema Guard
# never was, and assets.win.json is for vpk upload. CinemaGuard-Setup.exe carries Velopack's setup inside
# it, so that one stays only with -NoInstaller. A portable zip can be left by a build from before --noPortable.
$extra = @("RELEASES", "assets.win.json", "WaveBit.CinemaGuard-win-Portable.zip")
if (-not $NoInstaller) { $extra += "WaveBit.CinemaGuard-win-Setup.exe" }
foreach ($name in $extra) {
    $file = Join-Path $ReleasesDir $name
    if (Test-Path $file) {
        Remove-Item $file -Force
        Write-Host "Removed $name"
    }
}

# ---------------------------------------------------------------- summary

Step "Done"

Get-ChildItem $ReleasesDir -File |
    Sort-Object Name |
    Select-Object Name, @{ Name = "Size"; Expression = { if ($_.Length -ge 1MB) { "{0:N1} MB" -f ($_.Length / 1MB) } else { "{0:N0} KB" -f ($_.Length / 1KB) } } } |
    Format-Table -AutoSize

Write-Host ""
Write-Host "Upload everything in (it holds only what the server needs):" -ForegroundColor Cyan
Write-Host "  $ReleasesDir"
Write-Host "to the IIS folder that serves:"
Write-Host "  $FeedUrl"
Write-Host "and delete the older .nupkg files there; releases.win.json no longer lists them."
Write-Host ""
Write-Host "Users download CinemaGuard-Setup.exe once; after that the app updates itself."
Write-Host "The version list is releases.win.json; do not rename it."
