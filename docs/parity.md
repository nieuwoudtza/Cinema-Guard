# Cinema Guard parity run — 31 October / 1 November 2026

The old WinForms app and the new Cinema Guard were run side by side on three real films, on separate copies
of the same files, with the same word lists and subtitle source. This is the Phase 14 check from the upgrade
plan.

## Test files

The three films are referred to as A, B and C. Their titles do not matter to any of the numbers below.

| Film | Length | Size | Video | Audio | Subtitles used |
|---|---|---|---|---|---|
| A | 98.1 min | 1.86 GB | HEVC 1920×800 | E-AC3 5.1(side), Spanish first, English second | English full track (stream 7), extracted to `.srt` |
| B | 126.1 min | 2.11 GB | HEVC 1920×804 | AAC 5.1 | English full embedded track (stream 2), extracted to `.srt` |
| C | 145.0 min | 2.67 GB | H.264 1920×800 | AAC 5.1 | An English `.srt` for the same file (1,777 cues) |

There was no stereo copy; all three are 5.1. That is the better test for this app, and no stereo file
is needed.

## Settings used by both

- Word lists from the real old `cg.dat`: **16 "mute the whole sentence"** entries and **68 "mute the
  word"** entries.
- MKV output, Dual subtitles (Full + Censored), embedded.
- Vocals-only mode **off** (whole-track mute), original audio **not** kept, skip-clean **off**,
  delete-originals **off** — so both apps do the same work and nothing in the library is touched.
- Old app: Whisper `Large-v2` on CUDA, its bundled 2021 FFmpeg, `_OLD` affix.
- New app: Phonon 2.5 English + Phonon 2.5 multilingual, both on the CPU, FFmpeg 9.0.2, `_NEW` affix.

## Pass 1 — subtitle-driven, all three films

Each app read the same English `.srt`, so this isolates the matching, mute and encode pipeline.

| Film | Old app | New app |
|---|---|---|
| A | 0:57 | 0:40 |
| B | 4:38 | 4:45 |
| C | 5:11 | 4:27 |
| **Total** | **10:46** | **9:52** |

Per-film times are from the output completion times; the totals were measured with the stopwatch.
The new app was **8.4 % faster overall**, and the one film where it was slightly slower (B) is
the one where it wrote a faithful AAC track instead of the old app's smaller Vorbis one.

### Hits and subtitles

| Film | Old visible hit cues | New hit cues | New hit words / ranges |
|---|---|---|---|
| A | 14 | 17 | 17 / 17 |
| B | 48 | 53 | 63 / 53 |
| C | 16 | 21 | 34 / 21 |

The visible-cue gap is **not** missed words. The old app's saved `CensorCharacter` is a **space**, so
a mask at the start or end of a cue disappears when the subtitle is written (and FFmpeg trims it).
For example:

```
source:  Fucking disgusting.
old:              disgusting.        (mask was spaces; trimmed away)
new:     ******* disgusting.
```

Reading the source cue at every "old missing" time shows the old app did mask that cue. The hit sets
match. The new app also treats an already-masked source word (`f***`, `s**t`) as a hit, which the old
app does not; that accounts for the extra masked characters in the totals.

The new app's `*` censor character keeps the mask readable; the old app's space left holes in the
sentence.

Both apps write a blank cue for every non-hit line in the Censored track, so the track keeps the
full subtitle timeline. That is the old app's workaround for players (VLC and Plex) that misbehave
when the clean cues are missing, and the new app keeps it on purpose. The new app's benchmark output
for this run was made before the workaround was restored, so its Censored track at that time had only
the hit cues; the final build writes all of them, with a non-breaking space (U+00A0) for the clean
ones. The hit cues, mute ranges and audio are unchanged.

## Pass 2 — full transcription on film B

No shortcut here: both apps transcribed the 126-minute film themselves and then merged their
transcript with the same English sidecar.

| | Old app | New app |
|---|---|---|
| Model / device | Whisper large-v2, CUDA | Phonon 2.5 English + multilingual, CPU |
| Time | **10:28** | **9:29** |
| Transcript words | 8,386 | **8,992 (+7.2 %)** |
| Generated cues | 1,615 | 1,487 |
| Longest cue | **42.76 s** | **7.00 s** |
| Shortest cue | 0.06 s | 0.80 s |
| Mean reading speed | 16.2 CPS | **12.2 CPS** |
| Cues over 21 CPS | **329** | **47** |
| Hit cues | 53 | 53 |
| Masked runs in the subtitle track | 56 | 56 |
| Masked characters | 192 | 220 |
| Audio mute ranges | 53 cue-level | 123 word hits in 57 ranges |

The two apps muted the **same 53 subtitle cues**, within 1 second at every cue (the new cue times are
3–5 ms earlier because of SRT rounding). The new app's extra ranges come from the transcript words it
heard that the sidecar does not contain; those are audio-only mutes. The new app's cue builder also
keeps every cue inside 7 seconds and under 21 CPS, while Whisper produced a 42.76-second cue and 329
cues over the reading-speed limit.

### Generated subtitle accuracy (quick check)

Reference: the extracted English subtitle for film B (1,616 cues, **9,535 words**). A word counts as
matched when the same normalised word appears in the model's transcript within **±2 seconds** of the
reference cue word (reference word times are interpolated across each cue, because the subtitle has
cue-level timing only). This is a lexical comparison, not a true verbatim WER.

| Model | Words | Recall | Precision | F1 | In-order (LCS) | Subtitle-relative WER | Cue coverage | Profanity recall | Profanity precision | Median Δt |
|---|---|---|---|---|---|---|---|---|---|---|
| Whisper large-v2 (old) | 8,386 | 82.4 % | **93.4 %** | **87.6 %** | 79.5 % | **24.6 %** | 82.6 % | 58 / 107 (54.2 %) | 91.4 % | 0.23 s |
| Phonon 2.5 English (new) | 9,009 | **83.7 %** | 88.8 % | 86.1 % | **79.6 %** | 27.1 % | **89.4 %** | **67 / 107 (62.6 %)** | **93.5 %** | 0.28 s |

Reading it:

- **Whisper has a slight edge on precision, F1 and the subtitle-relative WER.** That is largely because
  it emits fewer words (8,386 vs 9,009). Subtitles leave out filler and repeats; every extra word
  Phonon correctly hears but the subtitle omits lowers its precision and raises its WER. This metric
  cannot distinguish "extra correct word" from "hallucination".
- **Phonon has higher recall, higher cue coverage and catches 9 more profanity occurrences**, with
  slightly better profanity precision (93.5 % vs 91.4 %). Its extra catches are spread across the film,
  not one repeated hallucination.
- **In-order accuracy is a tie** (79.5 % vs 79.6 %).
- **Timing of matched profanity** is within a quarter-second of the interpolated subtitle time for
  both; the 0.05 s difference is below the accuracy of the reference timing itself.

For this app, the practical result is the one that matters: on the full-transcription pass both models
muted the **same 53 cues**, and Phonon heard more of the profanity. Whisper is a 3 GB model running on
CUDA here; Phonon 2.5 English is a 178 MB model running on the CPU.

Caveat: the reference is a paraphrased subtitle track, not a verbatim transcript, so these percentages
are useful for comparing the two models against the same reference, not as an absolute transcription
score. A true WER needs a human-verified transcript.

## Audio quality

The old app's audio output is the weakest part of the comparison:

- **AAC sources became Vorbis.** B and C (AAC 5.1) were re-encoded to Vorbis 5.1 by
  the old app; the new app keeps AAC 5.1.
- **Channel layout was broken.** On a 10-second dialogue segment of B, the new app's six
  output channels correlate **1.000, 1.000, 1.000, 0.999, 1.000, 1.000** with the source channels.
  The old app's diagonal correlations are **0.05, −0.11, −0.11, −0.40, −0.10, −0.23** — the mix is
  not a faithful copy of any source channel.
- **Dual-audio film: the old app muted the Spanish track.** Film A has Spanish as audio 1 and
  English as audio 2. The old app's stream picker takes the first 5.1 track, then writes
  `language=eng` from subtitle language detection, so the file says English while the audio is not.
  The new app picks the English track by language.
  - New output vs English source, centre channel: **0.984** correlation.
  - Old output vs Spanish: 0.36; vs English: 0.41 (neither is a match).
- The new app also keeps the source codec's channel count and layout (E-AC3 5.1(side) stays
  E-AC3 5.1(side), AAC 5.1 stays AAC 5.1).

## Output files

| Film | Old output | New output |
|---|---|---|
| A | 1.81 GB, E-AC3 5.1(side) | 1.68 GB, E-AC3 5.1(side) |
| B | 1.97 GB, Vorbis 5.1 | 2.10 GB, AAC 5.1 |
| C | 2.52 GB, Vorbis 5.1 | 2.67 GB, AAC 5.1 |

The new files are larger only because they keep the source audio codec at its proper bitrate instead
of the old app's smaller Vorbis transcode. Video is copied untouched in both.

## Bugs confirmed by the run

| Bug | Evidence |
|---|---|
| B16 — mute targets the first audio track, not the preferred language | Film A: old app processed the Spanish track, labelled it English; new app processed English |
| B8 — text parsing of `ffmpeg -i` / channel layout | Old app's output channel correlations around zero on AAC 5.1; new app 1.000 on the diagonal |
| B15 — no hits / codec policy re-encodes with the wrong codec | Old app turned AAC 5.1 into Vorbis 5.1 on two films; new app keeps AAC |
| Old censor character is a space | Masked words vanish at cue starts/ends and the Censored track is mostly blank cues |
| Subtitle default flag | Old app marked Full as default; the new app did not in this run. Fixed after the run: `FfmpegCommands` now sets `-disposition:s:0 default` (and `0` for the second track) |

The last-cue bug (B4) and the end-of-cue masking bug (B3) did not show up as missed words in these
three files; every old hit had a new hit, and vice versa.

## Verdict

- **Accuracy:** the new app muted every cue the old app muted on the full-transcription film, and the
  same cues on the other two once the old app's invisible space masks are accounted for. It also
  caught 7.2 % more transcript words.
- **Speed:** 8.4 % faster on the subtitle pass and 59 seconds faster on the full 126-minute
  Whisper-vs-Phonon pass, with the new app on CPU and the old app on CUDA.
- **Quality:** the new app's audio is faithful and its censored subtitles are readable; the old app's
  audio is wrong on 5.1 AAC and dual-audio files, and its space masks leave holes in the text. Both
  apps keep the full cue timeline in the Censored track, with a non-breaking space (U+00A0) for the
  clean lines: the new app now keeps that workaround too, because VLC and Plex need it.

The new app is the one to keep. The only parity item left from this run is the subtitle default flag,
which is fixed in the source (the benchmark outputs above were produced before that fix).
