# Cinema Guard 2

Mutes filtered words in a film by writing a new file next to it. Speech comes from a model you pick
on the **Models** page; FFmpeg does the audio. The video is never re-encoded.

This is the WinUI 3 replacement for the old WinForms app in `..\Cinema Guard (Old)`.

## Layout

| Path | What |
|---|---|
| `Cinema Guard/` | WinUI 3 app (unpackaged, x64) |
| `src/CinemaGuard.Core/` | All logic: subtitles, speech engines, FFmpeg, pipeline. No UI. |
| `tests/CinemaGuard.Core.Tests/` | xUnit tests (103) |
| `installer/CinemaGuard.Installer/` | `CinemaGuard-Setup.exe`: folder choice, desktop shortcut, then Velopack install |
| `publish.ps1` | Builds a release for the WaveBit update server |
| `deploy/web.config` | IIS settings for a standalone release folder (`.nupkg` MIME type, no-cache); `publish.ps1 -IisWebConfig` |
| `Cinema Guard/tools/Import-CgDat.ps1` | Reads the old app's `cg.dat` (Windows PowerShell 5.1) |

## Build and run

```powershell
dotnet build "Cinema Guard.slnx" -c Debug
dotnet test "tests\CinemaGuard.Core.Tests\CinemaGuard.Core.Tests.csproj"

& ".\Cinema Guard\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Cinema Guard.exe"
```

Publish a self-contained build (the .NET runtime is inside the folder; Windows App SDK is bundled):

```powershell
dotnet publish "Cinema Guard\Cinema Guard.csproj" -c Release -p:Platform=x64 -p:PublishProfile=win-x64
# -> Cinema Guard\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\
```

Build and pack an update release:

```powershell
.\publish.ps1 2.0.1
# first release, no feed to download a delta from:
.\publish.ps1 2.0.0 -SkipPreviousDownload
```

## Using it

1. **Set up** on first launch. The Tools page has one button that installs FFmpeg and downloads the
   recommended Phonon pair. Defaults are already selected; there is nothing else to configure.
2. **Add files** or drag videos onto the window. Folders ask whether to include subfolders.
3. **Generate** means "transcribe this file". It starts ticked when the file has no subtitles,
   unless "Always generate subtitles" is on.
4. **Censor** runs the queue. One file at a time; one failure never stops the rest.
5. The hit count opens **Peek** (words, counts, exact times, and whether they came from the English
   model, the multilingual model, the subtitle file, or the source's own masking).

Output is `<name><affix>.<mp4|mkv>` (default `Movie_CG.mp4`). Subtitles:

- `Movie.srt` - the sidecar the app reads (yours, extracted, or downloaded).
- `Movie.generated.srt` - cues made from the transcript.
- `Movie.phonon-en.json`, `Movie.phonon-ml.json` - Phonon transcript caches (reused next time).
- `Movie.transcript.json` - transcript cache for Whisper, Parakeet or your server.
- `Movie.languages.json` - the foreign-dialogue report.
- `Movie_Full_CG.srt`, `Movie_Censored_CG.srt` - when not embedding.

## Speech models

The Models page offers one choice or the recommended pair, and downloads/removes every file itself:

| Family | Runs on | Notes |
|---|---|---|
| **Phonon 2.5** (English + multilingual pair) | CPU, or the GPU through Docker when installed | Best word timing; the default |
| **Parakeet** (v2 English, v3 25 languages) | CPU | Fast; no GPU pack (it was slower than the CPU and 3.6 GB) |
| **Whisper** (tiny to large-v3-turbo) | CPU, or the GPU automatically | Most languages; word timings are the loosest |
| **Your own server** | Another machine or a provider | Any OpenAI-compatible `/v1/audio/transcriptions` with word timestamps, e.g. `fermion serve` |

Docker is optional: it is only used when a Phonon row's **GPU** switch is on and Docker Desktop is
installed. Without it, Phonon runs on the CPU.

Models are stored in `%LOCALAPPDATA%\CinemaGuard\models`; engines in `engines`. Both can be deleted
from the Models page. The first run asks whether to download the recommended pair.

## Command line

```
Cinema Guard.exe "D:\Films\Heat (1995).mkv"          # opens the queue with the file
Cinema Guard.exe "D:\Films\Heat (1995).mkv" -start   # headless run, no window, exit code 0
```

`-start` writes `%LOCALAPPDATA%\CinemaGuard\logs\last-run.json` with the result of every file. This
is the mode to call from another program (MediaPilot, for example). A second GUI launch hands its
paths to the running window instead of starting again.

## What it needs

| Tool | Why | Where |
|---|---|---|
| FFmpeg + FFprobe 7.1+ | probe, mute, encode, extract | installed by the app on the Tools page (one click); an existing FFmpeg 7.1+ on PATH is used instead |
| Docker Desktop + `ghcr.io/fermionresearch/phonon-cuda:1.0.11` | Phonon on the GPU (optional) | started on demand |
| `subloader-cli` | OpenSubtitles download (optional) | `C:\Program Files\Subloader` |

A speech model is downloaded from the Models page, not installed with the app. Tools shows the
status of everything; Settings holds overrides for every path.

## How a file is processed

1. Probe with FFprobe; pick the film's main audio and subtitle track (preferring the preferred
   language, never a forced or commentary track).
2. Subtitles: your file, a sidecar, an embedded text track, or the speech model.
3. Transcribe with the chosen model (or the Phonon pair). Engines stay loaded for the whole queue.
4. Filter hits: words from the transcript(s) plus the subtitle file. Subtitle timings are first
   fitted to the transcript, which fixes downloads timed for 24.000 fps on a 23.976 fps film.
5. Write masked subtitles and encode: mute the selected track at the hit ranges, copy the video,
   validate the result, then rename `.partial` to the final name.
6. Only after validation: the Recycle Bin option applies, never a permanent delete.

Vocals-only mode splits the 5.1/7.1 track, analyses each dialogue channel separately, silences only
the channels that carry the word, and joins the channels back in one encode.

## Where things live

- Settings: `%LOCALAPPDATA%\CinemaGuard\settings.json`
- Models and engines: `%LOCALAPPDATA%\CinemaGuard\models`, `...\engines`
- FFmpeg installed by the app: `%LOCALAPPDATA%\CinemaGuard\tools\ffmpeg`
- Logs: `%LOCALAPPDATA%\CinemaGuard\logs\` (every command line and error)
- Temporary work: `%LOCALAPPDATA%\CinemaGuard\work\` (deleted after each file, older than 2 days on start)

## Updates

Installed copies update themselves with [Velopack](https://velopack.io/) from
`https://wavebit.dev/software/cinemaguard`. The feed is a plain folder outside the site's `wwwroot`,
served by the wavebit.dev MVC app with `UseStaticFiles` (`.nupkg` mapped to
`application/octet-stream`); upload everything `publish.ps1` writes. `releases.win.json` is the
version list. Only a standalone IIS site or virtual directory needs `deploy/web.config`
(`publish.ps1 -IisWebConfig`). `CINEMAGUARD_UPDATE_FEED` points a test install at another folder or
staging URL.

## Tests

```powershell
dotnet test "tests\CinemaGuard.Core.Tests\CinemaGuard.Core.Tests.csproj"
```

Covers the SRT/VTT/ASS parser, the matcher and masking (including the old off-by-one at the end of a
cue), mute-range merging, subtitle drift alignment, FFmpeg argument construction, the codec policy,
the cue builder, the speech catalogue and downloader, and the settings/output-path rules.
