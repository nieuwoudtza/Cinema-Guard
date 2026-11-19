# Changelog

What changed in each version of the Cinema Guard browser extension, newest first. The options page shows this
list under What's new, and the popup points to it after an update.

Each version is a `## <version> — <date>` heading followed by one `- ` line per change. Add changes under
`## Unreleased`; `publish-extension.ps1` turns that heading into the version it packs.

## 1.0.5 — 19 November 2026

- Choose where Cinema Guard filters, under Sites and channels: everywhere except the sites on your blacklist (the default), or only the sites on your whitelist. Sites paused before are on the blacklist, and sites you had turned on one by one are on the whitelist.
- No more turning sites on one by one: Cinema Guard runs on every site and always reads the subtitles, so adding a site to the whitelist or taking it off the blacklist works at once, also on a video already playing. In whitelist mode the toolbar button asks whether to add a site that isn't on it. The browser asks once to allow Cinema Guard on all sites.
- Players embedded from another site, as on most film sites, are filtered as the site you are on says.
- Fixed: on many film sites (such as popcornmovies.st) the sound was muted but the swear words still showed in the subtitles. Subtitles are now blanked wherever the player draws them: more players are known (Vidstack, ArtPlayer, DPlayer, xgplayer, Fluid Player, Kaltura, THEOplayer), elements named like captions over the video count too, and with a subtitle file any text that is part of the line being spoken is blanked, whatever draws it, also inside web components.
- Subtitle files are recognised in more places, and the toolbar popup follows the film rather than an advert playing beside it.
- Fixed: when a player started with subtitles in another language (often Arabic), Cinema Guard read those. Only subtitles in your caption language are read now, also files and tracks that don't say their language; switch the player to your language and filtering starts at once.
- New setting for dubbed sound, under Captions: Stop muting when the subtitles aren't in my language. When you switch a film's subtitles to another language, muting pauses and the popup's detections clear, until you switch back. Off by default, since switching only the subtitles leaves the original sound, which is still muted.
- Pages without a video are left alone: Cinema Guard only starts its work once a video turns up.
- The popup shows "Muting now" the moment the sound goes and stops the moment it's back (it lagged by about half a second), in the status corner of the video card, so nothing below it moves.
- A cleaner look for status tags in the popup and the options page.

## 1.0.4 — 11 November 2026

- Fixed: in YouTube's new player, hidden captions looked the same as captions in view, so the CC button seemed on while nothing showed. The button is now dimmed and struck through while captions are kept on out of sight; press it (or C) to show them.
- Fixed: on some YouTube accounts no captions showed at all while they were on, with CC pressed and a track chosen. The extension pressed CC while the video was still loading, before YouTube had set up its captions; it now waits until the video has played for a second, and YouTube often turns them on by itself before then. Should captions still not show while a line is due, the extension has YouTube reload them and says so in the log.
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
