# Imports the old Cinema Guard settings file (cg.dat) into JSON for Cinema Guard 2.
# Runs under Windows PowerShell 5.1, which still has BinaryFormatter.
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File Import-CgDat.ps1 `
#       -OldExe "C:\path\Cinema Guard.exe" -CgDat "C:\path\cg.dat" -OutJson "C:\path\legacy.json"
param(
    [Parameter(Mandatory = $true)][string]$OldExe,
    [Parameter(Mandatory = $true)][string]$CgDat,
    [Parameter(Mandatory = $true)][string]$OutJson
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $OldExe)) { throw "Old Cinema Guard executable not found: $OldExe" }
if (-not (Test-Path -LiteralPath $CgDat)) { throw "Settings file not found: $CgDat" }

$assembly = [System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $OldExe).Path)

# BinaryFormatter has to resolve Cinema_Guard.Settings from the old assembly.
$resolve = {
    param($sender, $eventArgs)
    $simple = $eventArgs.Name.Split(',')[0].Trim()
    if ($simple -eq 'Cinema Guard' -or $simple -eq 'Cinema_Guard') { return $assembly }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler]$resolve)

try {
    $base64 = (Get-Content -LiteralPath $CgDat -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($base64)) { throw "Settings file is empty: $CgDat" }

    $bytes = [System.Convert]::FromBase64String($base64)
    $stream = New-Object System.IO.MemoryStream(, $bytes)
    $formatter = New-Object System.Runtime.Serialization.Formatters.Binary.BinaryFormatter
    $settings = $formatter.Deserialize($stream)
    $stream.Dispose()

    if ($null -eq $settings) { throw "Settings could not be read from $CgDat" }

    $json = [ordered]@{
        OutputType               = [string]$settings.OutputType
        CustomAffix              = [string]$settings.CustomAffix
        AspectRatio              = [bool]$settings.AspectRatio
        AspectRatioWidth         = [int]$settings.AspectRatioWidth
        AspectRatioHeight        = [int]$settings.AspectRatioHeight
        CreateSubtitles          = [bool]$settings.CreateSubtitles
        SubtitlesType            = [string]$settings.SubtitlesType
        EmbedSubtitles           = [bool]$settings.EmbedSubtitles
        WordsFull                = @($settings.WordsFull)
        WordsSingle              = @($settings.WordsSingle)
        StandardSingleWordBefore = [int]$settings.StandardSingleWordBefore
        StandardSingleWordAfter  = [int]$settings.StandardSingleWordAfter
        OneWordSingleWordBefore  = [int]$settings.OneWordSingleWordBefore
        OneWordSingleWordAfter   = [int]$settings.OneWordSingleWordAfter
        CensorCharacter          = [string]$settings.CensorCharacter
        Language                 = [string]$settings.Language
        MuteVocalsOnly           = [bool]$settings.MuteVocalsOnly
        ForceSameAudioEncoding   = [bool]$settings.ForceSameAudioEncoding
        KeepOriginalAudio        = [bool]$settings.KeepOriginalAudio
        SkipMediaNoSwearing      = [bool]$settings.SkipMediaNoSwearing
        DeleteOriginalFiles      = [bool]$settings.DeleteOriginalFiles
        AlwaysGenerateSubtitles  = [bool]$settings.AlwaysGenerateSubtitles
        AlwaysOverwriteSubtitles = [bool]$settings.AlwaysOverwriteSubtitles
    }

    $parent = Split-Path -Parent $OutJson
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $json | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutJson -Encoding UTF8
    Write-Output $OutJson
}
finally {
    [System.AppDomain]::CurrentDomain.remove_AssemblyResolve([System.ResolveEventHandler]$resolve)
}
