# Cinema Guard

**Watch the films you like, without the language you don't.**

Cinema Guard is a free Windows app that finds swear words and other words you choose in a film,
and makes a copy of the film with those words muted. The rest of the film is untouched: the picture
is copied as is, so there's no loss in quality, and your original file stays where it was.

It's made for families, for watching with kids, or for anyone who would rather skip the language.

## What it does

- **Listens to the film.** A speech model on your own PC writes down every word, with exact
  timings, so it catches words the subtitles leave out.
- **Uses subtitles too.** It can read the subtitles inside the video, a subtitle file next to it,
  or download one, and it lines them up with what's actually spoken.
- **Mutes only the word.** Just the moment the word is spoken goes quiet, not the whole scene.
  With *vocals-only* mode on a 5.1 or 7.1 film, it silences only the speaker channels carrying the
  word, so music and effects keep playing.
- **You choose the words.** Tick whole word families on the Filter page, or add your own words and
  phrases.
- **Clean subtitles as well.** It can add subtitles with the filtered words blanked out.
- **Shows its work.** Click the hit count to see every word it found and exactly when.
- **Works through a whole library.** Drop in films or entire folders and let it run; one problem
  file never stops the rest. It can put the PC to sleep, hibernate, restart or shut it down when the queue is done.
- **Audiobooks and podcasts too.** MP3, M4B, FLAC and other audio files are cleaned the same way
  and keep their format, cover art and chapters.

## Download

**[Download CinemaGuard-Setup.exe](https://wavebit.dev/software/cinemaguard/CinemaGuard-Setup.exe)**

Run the setup, choose a folder, and you're done. The app keeps itself up to date.

Requirements: Windows 10 (version 1809 or later) or Windows 11, 64-bit. A graphics card can make transcription
faster, but isn't needed.

## How to use it

1. **First launch:** click the one button on the Tools page. It installs FFmpeg and downloads the
   recommended speech models. Everything else is already set up.
2. **Add your films:** click Add files, or drag videos onto the window.
3. **Click Censor.** Films are done one at a time, and you can add or remove files while it runs.
4. **Watch.** The cleaned copy is saved next to the original, for example `Movie_CG.mp4` next to
   `Movie.mp4`.

## Good to know

- **Your original is safe.** Cinema Guard always writes a new file. It only removes the original if
  you tell it to in Settings, and only after it has checked that the new file is good.
- **It stays on your PC.** Speech recognition runs on your own computer. The internet is used only
  to download the tools and models, to check for updates, and to fetch subtitles if you ask it to.
- **No model is perfect.** Speech recognition can miss a word, especially under loud music or
  shouting. Using subtitles as well catches most of what the model misses.
- **Re-runs are quick.** Transcripts are kept for 30 days, so changing your word list and running a
  film again skips the slow part.

## Questions, bugs and ideas

Open an [issue](https://github.com/nieuwoudtza/Cinema-Guard/issues). Pull requests are welcome too;
see [Contributing](#contributing).

---

## For developers

Everything below is about how Cinema Guard is built. You don't need any of it to use the app.

Cinema Guard is a WinUI 3 app on .NET 10. It replaces an older WinForms app, which is not part of
this repository.

### Project layout

| Path | What |
|---|---|
| `Cinema Guard/` | WinUI 3 app (unpackaged, x64) |
| `src/CinemaGuard.Core/` | All logic: subtitles, speech engines, FFmpeg, pipeline. No UI. |
| `tests/CinemaGuard.Core.Tests/` | xUnit tests (217) |
| `installer/CinemaGuard.Installer/` | `CinemaGuard-Setup.exe`: folder choice, desktop shortcut, then Velopack install |
| `publish.ps1` | Builds a release for the WaveBit update server |
| `deploy/web.config` | IIS settings for a standalone release folder (`.nupkg` MIME type, no-cache); `publish.ps1 -IisWebConfig` |
| `Cinema Guard/tools/Import-CgDat.ps1` | Reads the old app's `cg.dat` (Windows PowerShell 5.1) |

### Build and run

```powershell
dotnet build "Cinema Guard.slnx" -c Debug
dotnet test "tests\CinemaGuard.Core.Tests\CinemaGuard.Core.Tests.csproj"

& ".\Cinema Guard\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Cinema Guard.exe"
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

### The queue in detail

- **Generate subtitles** means "transcribe this file with the speech model"; **Extract subtitles**
  means "use the subtitle track inside the video". New files start with whatever Settings → Files
  says (Generate off, Extract on by default).
- Folders ask whether to include subfolders. A folder's audio files are only added when it holds no
  video, so a film's `theme.mp3` is left alone.
- **Audio files** (MP3, M4B, M4A, AAC, FLAC, WAV, OGG, OPUS, WMA) are always transcribed and written
  back in their own format (AAC and WMA become M4A), with cover art, chapters and tags; they get no
  subtitle tracks.
- **When finished** (the footer) sleeps, hibernates, restarts or shuts down the PC after the queue has run to the end,
  with a one-minute countdown that can be cancelled. It is chosen per run and never saved; Stop
  never triggers it.
- **Peek** (the hit count) lists words, counts, exact times, and whether each came from the English
  model, the multilingual model, the subtitle file, or the source's own masking.

Output is `<name><affix>.<mp4|mkv>` (default `Movie_CG.mp4`). Beside the video, only:

- `Movie.srt` - the sidecar the app reads (yours, saved from the video, or downloaded).
- `Movie_Full_CG.srt`, `Movie_Censored_CG.srt` - when not embedding.
- `Movie.oneword.srt` - only with "Also write one-word subtitles".

Inside the output, after the censored audio and the Full/Censored subtitles:

- The source's **forced** subtitle track (Settings → Subtitles, on by default), masked with the
  filter and flagged forced. An image track (PGS, VobSub) cannot be masked and is copied; MP4
  cannot hold one at all, which the row's message then says.
- With "Keep the other audio tracks" / "Keep the other subtitle tracks" (off by default), the
  rest of the source's tracks, copied unchanged and **not censored**. They never take the default
  flag from the censored track. MKV also keeps the font attachments ASS tracks need; a kept DTS or
  TrueHD track moves the output to MKV.

The **Filter** page's families open (the "3 of 7" count) to turn single spellings on or off.

Transcripts, the generated subtitles and the foreign-dialogue report are kept in
`%LOCALAPPDATA%\CinemaGuard\cache\<film>-<hash>\` so a re-run skips the speech model ("Keep
transcripts for re-runs"; unused ones go after 30 days). Files an earlier version left beside the
video are moved there.

### Speech models

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

### Command line

```
Cinema Guard.exe "D:\Films\Heat (1995).mkv"          # opens the queue with the file
Cinema Guard.exe "D:\Films\Heat (1995).mkv" -start   # headless run, no window, no dialogs
Cinema Guard.exe --help                              # every option and exit code (also -h, -?, /?)
```

`--help` prints to the console it was started from (or a redirected stdout), and shows a message
box when there is none. The same text (`LaunchArguments.HelpText`) is in Settings → Command line →
Show options, with a Copy button.

`-start` writes `%LOCALAPPDATA%\CinemaGuard\logs\last-run.json` with the result of every file. This
is the mode to call from another program (MediaPilot, for example). A second GUI launch hands its
paths to the running window instead of starting again.

A headless run also takes these options:

| Option | What it does |
|---|---|
| `--report <path>` | Writes the same report to `<path>` as well (written to a temp file and moved in, so a reader never sees half of it) |
| `--progress` | One JSON line on stdout per change of step or whole percent, at most one a second, plus every status change: `{"file":"…","status":"Running","step":"Encoding","progress":42.5}` |
| `--cancel-event <name>` | Stops the run when the caller signals this named event (`Local\…`). The file being worked on is cancelled cleanly, so no `.partial` output is left behind |

Its exit code: `0` every file succeeded or was skipped, `1` at least one failed or was cancelled,
`2` no video or audio files in the arguments, `3` the run itself crashed (see the log). The report carries the
same `exitCode`.

While a headless run is alive, the window does not install a downloaded update (on restart, on close
or at startup), because that replaces the folder the run executes from; the update waits for the
next start. A headless launch never installs one either.

Older builds treat these options as paths that do not exist and ignore them, and always exit with 0.

### External tools

| Tool | Why | Where |
|---|---|---|
| FFmpeg + FFprobe 7.1+ | probe, mute, encode, extract | installed by the app on the Tools page (one click); an existing FFmpeg 7.1+ on PATH is used instead |
| Docker Desktop + `ghcr.io/fermionresearch/phonon-cuda:1.0.11` | Phonon on the GPU (optional) | started on demand |
| `subloader-cli` | OpenSubtitles download (optional) | `C:\Program Files\Subloader` |

A speech model is downloaded from the Models page, not installed with the app. Tools shows the
status of everything; Settings holds overrides for every path.

### How a file is processed

1. Probe with FFprobe; pick the film's main audio and subtitle track (preferring the preferred
   language, never a forced or commentary track).
2. Subtitles: your file, a sidecar, an embedded text track, or the speech model.
3. Transcribe with the chosen model (or the Phonon pair). Engines stay loaded for the whole queue.
4. Filter hits: words from the transcript(s) plus the subtitle file. Subtitle timings are first
   fitted to the transcript, which fixes downloads timed for 24.000 fps on a 23.976 fps film. The
   subtitle file is then matched to the transcript word by word (shared phrases as anchors,
   Needleman–Wunsch between them), so a word only the subtitles have, such as a masked `f***` or a
   word the model heard as another, is muted between the spoken words either side of it.
5. Write masked subtitles and encode: mute the selected track at the hit ranges, copy the video,
   validate the result, then rename `.partial` to the final name.
6. Only after validation: the originals are kept, sent to the Recycle Bin or deleted permanently,
   as chosen in Settings (Keep is the default).

Vocals-only mode splits the 5.1/7.1 track, analyses each dialogue channel separately, silences only
the channels that carry the word, and joins the channels back in one encode.

### Where things live

- Settings: `%LOCALAPPDATA%\CinemaGuard\settings.json`
- Models and engines: `%LOCALAPPDATA%\CinemaGuard\models`, `...\engines`
- FFmpeg installed by the app: `%LOCALAPPDATA%\CinemaGuard\tools\ffmpeg`
- Logs: `%LOCALAPPDATA%\CinemaGuard\logs\` (every command line and error)
- Temporary work: `%LOCALAPPDATA%\CinemaGuard\work\` (deleted after each file, older than 2 days on start)

### Updates and releases

Installed copies update themselves with [Velopack](https://velopack.io/) from
`https://wavebit.dev/software/cinemaguard`. The feed is a plain folder outside the site's `wwwroot`,
served by the wavebit.dev MVC app with `UseStaticFiles` (`.nupkg` mapped to
`application/octet-stream`); upload everything `publish.ps1` writes. `releases.win.json` is the
version list. Only a standalone IIS site or virtual directory needs `deploy/web.config`
(`publish.ps1 -IisWebConfig`). `CINEMAGUARD_UPDATE_FEED` points a test install at another folder or
staging URL.

Releases are numbered by date, `yyyy.MM.dd.<iteration>`: `.\publish.ps1` with no arguments makes
`2026.11.01.1`, and a second release that day `2026.11.01.2` (it reads the feed and the local release
folder to find the next iteration). Velopack only accepts 3-part versions, so the package itself is
`2026.1101.1`; the app shows the dated form.

### Tests

```powershell
dotnet test "tests\CinemaGuard.Core.Tests\CinemaGuard.Core.Tests.csproj"
```

Covers the SRT/VTT/ASS parser, the matcher and masking (including the old off-by-one at the end of a
cue), mute-range merging, subtitle drift and word-by-word alignment, FFmpeg argument construction, the codec policy,
the cue builder, the speech catalogue and downloader, and the settings/output-path rules.

## Contributing

Issues and pull requests are welcome. Before sending one:

```powershell
dotnet build "Cinema Guard.slnx" -c Debug
dotnet test "tests\CinemaGuard.Core.Tests\CinemaGuard.Core.Tests.csproj"
```

Keep the build and the tests green, and match the style of the code around the change.

By submitting a pull request you agree to release your contribution under the project's license
(MIT No Attribution).

## License

[MIT No Attribution](LICENSE): use it, change it, sell it, ship it - no attribution required, no
warranty given. The WaveBit name and logo are not part of that grant.
