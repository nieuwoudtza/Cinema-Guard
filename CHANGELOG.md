# Changelog

What changed in each release of Cinema Guard for Windows, newest first. The app shows this list under
Settings → About → What's new, and shows the new entries on the first start after an update.

Each release is a `## <version> — <date>` heading followed by one `- ` line per change. Add changes under
`## Unreleased`; `publish.ps1` turns that heading into the version it releases. The browser extension has its
own list in [extension/CHANGELOG.md](extension/CHANGELOG.md).

## Unreleased

- What's new: Settings → About lists the changes in every release, and the first start after an update shows what is new since the version you had.
- The word families gain misspellings subtitles use: F*CIN, F*KIN, F*CKEN, SH*TTEST and JEEZ. Saved word lists that have the family get them too.

## 2026.11.06.1 — 6 November 2026

- Peek says what each run worked from: which subtitles, the speech model, how much of the subtitles matched the speech, and the audio mode, and why a track was muted whole.
- Choose subtitle track… (right-click a file in the queue) picks the subtitle track inside the video that Cinema Guard reads.
- Listen to channels… (right-click a file in the queue) plays 20 seconds of each audio channel on its own, to hear which ones carry the dialogue.
- Restore on the Tools page puts back the original audio of a film censored with Keep the original audio.
- New Exact word only setting (Settings → Filter) sets the single-word padding and the fade to 0.
- Tooltips across the app.
- Fixed: with every padding at 0, a word only the subtitles have muted nothing. It now gets its share of the line (#3).

## 2026.11.05.2 — 5 November 2026

- Mute only the dialogue channels also mutes a surround channel that carries a copy of the centre's dialogue, panned or as the room's reverb, even when that channel's own transcript hears nothing.
- New setting: Show subtitles when the video opens (Off, Full or Censored) picks the embedded track a player turns on by itself, VLC included.
- The word families gain spellings subtitles use, such as MOTHERF*CK, GODD*MMIT and D*PSHIT. Saved word lists that have the family get them too.
- FFmpeg's version is checked before a file is transcribed, so an FFmpeg that is too old stops the run at once with the reason, instead of failing at the end. An old copy on PATH is skipped for a newer one (#1).
- Fixed: a word the speech model stretched into the pause after it no longer mutes the whole gap up to the next line (#2).

## 2026.11.05.1 — 5 November 2026

- Updates install quietly: Cinema Guard closes, opens again as the new version, and says whether the update worked.
- Opening Cinema Guard again brings the open window forward, also from the taskbar or another virtual desktop.
- The Models page opens straight away and fills in what is installed as it finds it.
- The Filter page opens faster.
- Browser extension, at the bottom of the side menu, opens the extension in the Chrome Web Store.

## 2026.11.04.2 — 4 November 2026

- Subtitles are matched to the speech word by word, so a word only the subtitles have, such as a masked f*** or a word the speech model misheard, is muted between the spoken words either side of it. Peek shows how much of the subtitles matched. Ported from Petrus Vermaak's SRT Mapper.
- New Fade in and out setting (30 ms by default) fades the sound out before each mute and back in after it, so a mute never clicks.
- The Filter page stars every swear word inside compound words, not only the first.
- Cinema Guard is now also a browser extension for Chrome, Brave and Edge that mutes swearing in YouTube videos while you watch.

## 2026.11.03.4 — 3 November 2026

- Search the settings: the box at the top of the Settings page (Ctrl+F) shows only the settings that mention every word you type.

## 2026.11.03.3 — 3 November 2026

- Every page's content lines up at the same width, and the switches end where the other controls do.

## 2026.11.03.1 — 3 November 2026

- The censor character can be a space, which leaves a censored word blank in the subtitles.

## 2026.11.02.9 — 2 November 2026

- Audiobooks and podcasts: MP3, M4B, M4A, AAC, FLAC, WAV, OGG, OPUS and WMA files are cleaned like films and keep their format, cover art, chapters and tags (AAC and WMA become M4A).
- The video's forced subtitle track is kept, masked and flagged forced. Its other subtitle and audio tracks can be copied unchanged too (off by default; they are not censored).
- When finished: the queue can put the PC to sleep, hibernate, restart or shut it down when it is done, with a minute to cancel.
- The Filter page opens a word family to turn single spellings on or off.
- Command line: a -start run can write a report, print its progress and be cancelled by the program that started it, and its exit code says how it went. --help lists every option, as does Settings → Command line.
- An update waits while another program is running Cinema Guard in the background.
- The OpenSubtitles download limit is explained, with the time it resets.
- Cinema Guard is open source, under the MIT No Attribution licence.

## 2026.11.01.11 — 1 November 2026

- Phrases: an entry of several words, such as HOLY SH*T, is muted from its first word to its last, and is found across punctuation ("Holy... sh*t!").
- The queue: Select all, none or invert, Remove selected, a Generate subtitles tick box on each file, and a right-click menu. Files can be added, removed and cancelled while it runs.
- Each file and the whole queue show a percentage and the time left that mean something, as each step is weighed by how long it takes.
- Transcripts and generated subtitles are kept in Cinema Guard's own folder and reused, never beside the video.
- Generate subtitles and Extract subtitles are separate choices, on each file and as the defaults for new files.
- The queue shows which subtitles a file already has before its first run.
- The Tools page offers the recommended Docker image for Phonon on the graphics card. When Docker cannot be used, the rest of the queue goes straight to the CPU and says why.
- After censoring, the originals can be kept, sent to the Recycle Bin or deleted permanently.
- Save subtitles as .srt and Download subtitles can be stopped, and never replace a subtitle file already beside the video.
- The version is shown under Settings → About.

## 2026.11.01.1 — 1 November 2026

- The first release of the new Cinema Guard for Windows, rebuilt to replace the older app.
- Versions are numbered by date, and the app has its own icon.
