# Cinema Guard for the browser

Mutes swearing in online videos, using their captions: the same word lists, word families, matching and mute padding as the
Windows app. Works in Brave, Chrome and other Chromium browsers.

## Install

Add it from the [Chrome Web Store](https://chromewebstore.google.com/detail/cinema-guard/maidlcfjpdcmfbbkdbijfhegbbnoicfn).

## Install from this folder (load unpacked)

1. Open `brave://extensions` (or `chrome://extensions`).
2. Turn on **Developer mode** (top right).
3. Select **Load unpacked** and choose this `extension` folder.
4. Pin Cinema Guard to the toolbar (puzzle-piece menu → pin).

After pulling changes, select the reload arrow on Cinema Guard's card in `brave://extensions`, then reload any open video tabs.

If you used the Tampermonkey userscript, turn it off: both would mute and change captions at the same time.

## Where it works

| Site | How captions are read |
|---|---|
| YouTube (videos, Shorts, embeds on other sites) | Built in. The caption file is fetched in full; auto-generated captions give every word its own time |
| Any other site | Select Cinema Guard in the toolbar and choose **Turn on**. Uses the video's subtitle tracks, subtitle files the player downloads, or captions as they appear on screen |
| Netflix, Prime Video, Disney+, Max, Hulu | Not tested yet (they need paid accounts), so off until turned on from the toolbar (**Try it anyway**). Adapters exist in `page/sites.js`; reports go to the GitHub issues |

## Using it

- **Toolbar popup:** on/off switch, what was found in the video playing (select a line to jump to it), don't filter this
  channel, pause on this site, show or hide captions, and how the captions were found.
- **Options:** the filter (as in the Windows app), mute padding, the fade in and out around each mute (30 ms by
  default), censor character, caption language, YouTube caption behaviour, progress-bar marks, sites and channels,
  statistics, and backup. **Backup → Import** reads the Windows app's `%LOCALAPPDATA%\CinemaGuard\settings.json`.
- **What's new** (last in the options page's menu): what changed in each version. After an update the popup says so and
  links there, and the versions you have not seen yet are marked New.
- **Cinema Guard for Windows** cleans your own video files: <https://wavebit.dev/software/cinemaguard>.
- **Keyboard:** Alt+Shift+M turns filtering on or off (change it at `brave://extensions/shortcuts`).

## Layout

| Path | What it is |
|---|---|
| `shared/` | Word catalogue, matching, settings, caption parsers and mute timing (ports of the Windows app's core) |
| `page/` | Runs inside the page: request hooks, site adapters, the engine |
| `content/bridge.js` | Runs beside the page: settings in, state and statistics out |
| `background.js` | Tab state, badge, statistics, keyboard shortcut, sites turned on by the user |
| `popup/`, `options/`, `ui/` | The toolbar popup, the options page and their shared look |
| `CHANGELOG.md` | What changed in each version, read by What's new (`shared/changelog.js`). Add changes under `## Unreleased`; `publish-extension.ps1` names it after the version it packs |

Tests for the shared core: `node --test "tests/extension/*.test.mjs"` from the repository root.
