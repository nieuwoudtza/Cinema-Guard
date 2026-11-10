# Changelog

What changed in each version of the Cinema Guard browser extension, newest first. The options page shows this
list under What's new, and the popup points to it after an update.

Each version is a `## <version> — <date>` heading followed by one `- ` line per change. Add changes under
`## Unreleased`; `publish-extension.ps1` turns that heading into the version it packs.

## Unreleased

- Fixed: in YouTube's new player, hidden captions looked the same as captions in view, so the CC button seemed on while nothing showed. The button is now dimmed and struck through while captions are kept on out of sight; press it (or C) to show them.
- Fixed: YouTube sometimes drew no captions at all while they were on, with CC pressed and a track chosen. When the captions have a line and none has shown for a few seconds, the extension now has YouTube reload them, and says so in the log.
- The log on the popup says each time captions are hidden or shown, including from the popup, the options page or another tab.

## 1.0.3 — 10 November 2026

- What's new: the options page lists the changes in every version, and the popup says when the extension was updated.
- The word families gain misspellings captions use: F*CIN, F*KIN, F*CKEN, SH*TTEST and JEEZ. Saved word lists that have the family get them too, as do settings imported from the Windows app.
- Fixed: YouTube captions sometimes went out of sight by themselves. When YouTube turned them off while a video or an ad loaded, the extension took it as the viewer hiding them; now it only turns them back on.
- Fixed: with YouTube captions hidden, the C key could not show them again. It now shows and hides them as the CC button does.

## 1.0.2 — 6 November 2026

- The word families gain spellings captions use, such as MOTHERF*CK, GODD*MMIT and D*PSHIT. Saved word lists that have the family get them too, as do settings imported from the Windows app.

## 1.0.1 — 4 November 2026

- The filter stars every swear word inside compound words, not only the first.

## 1.0.0 — 4 November 2026

- The first version: mutes swearing in YouTube videos while they play, with the Windows app's word families, matching, extra mute and fade.
- Reads each video's caption file, and follows the captions on screen when there is none.
- Toolbar popup, an options page like the Windows app's, channels you leave unfiltered, marks on the progress bar, statistics and a keyboard shortcut (Alt+Shift+M).
- Works on Shorts and on YouTube videos embedded in other sites. Other sites can be turned on one at a time.
