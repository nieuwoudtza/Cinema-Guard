# Chrome Web Store listing: Cinema Guard

Everything the developer dashboard asks for, ready to paste. Images are in `images/`; the package to upload,
`cinema-guard-<version>.zip`, is written beside this file by `publish-extension.ps1`.

## Store listing tab

**Name:** Cinema Guard

**Summary** (132 characters at most; the store takes it from the manifest's `description`):

> Mutes swearing in videos as they play, using their captions: YouTube and most other video sites. Your own word list.

**Category:** Entertainment

**Language:** English

**Description:**

> Cinema Guard mutes swearing in videos while you watch, on YouTube and most film and video sites, so the whole family can enjoy them without the language.
>
> It reads a video's captions ahead of time, finds the words on your list and mutes just those moments. With YouTube's auto-generated captions every word has its own time, so only the word itself goes quiet. The sound fades out and back in over a few milliseconds, so a mute never clicks.
>
> WHAT IT DOES
> • Mutes only the words you choose: tick whole word families, pick single spellings, or add your own words and phrases.
> • Two kinds of mute: the word only (everyday swearing), or the whole sentence (blasphemy and insults).
> • Blanks the filtered words in the captions on screen too, with a character of your choice.
> • Marks on YouTube's progress bar show where a video will be muted.
> • Works on videos, Shorts, and YouTube videos embedded on other sites.
> • Leave channels you trust unfiltered. Filter everywhere except a blacklist of sites, or only on a whitelist.
> • See what was found in the video you're watching, and jump straight to any of it.
> • Statistics: how many words were muted, which ones, and in which videos.
> • Alt+Shift+M turns filtering on or off from any tab.
> • Your settings follow you to your other browsers through your browser's own sync.
>
> OTHER SITES
> Cinema Guard works on other sites with captions or subtitles too, including players embedded from another site. It reads the video's subtitle tracks, the subtitle files the player downloads, or the captions as they appear on screen, and blanks the words wherever the player draws its captions. Choose where it filters: everywhere except the sites on your blacklist, or only the sites on your whitelist.
>
> HELP WANTED: NETFLIX, PRIME VIDEO, DISNEY+, MAX AND HULU
> Cinema Guard knows how these services deliver their subtitles, but they need paid accounts and nobody has been able to test them yet. If you have one of these services and would like to help, play a video there and tell us how it went: https://github.com/nieuwoudtza/Cinema-Guard/issues
>
> PRIVATE BY DESIGN
> No account, no tracking, no data sent anywhere. Captions are read and matched inside your browser.
>
> FOR YOUR OWN FILMS
> Cinema Guard for Windows cleans your own video files the same way, with muted words and clean subtitles in a new copy: https://wavebit.dev/software/cinemaguard

**Images:**

| Field | File |
|---|---|
| Store icon (128 × 128, 96 × 96 artwork with transparent padding) | `images/store-icon-128.png` |
| Screenshots (1280 × 800, 24-bit PNG) | `images/screenshot-1-popup.png` … `images/screenshot-5-sites.png`, in that order |
| Small promo tile (440 × 280) | `images/promo-small-440x280.png` |
| Marquee promo tile (1400 × 560) | `images/promo-marquee-1400x560.png` |

The description is in `description.txt` as plain text, ready to paste.

**Official URL:** none (it needs a verified domain in Search Console; optional).
**Homepage URL:** https://wavebit.dev/software/cinemaguard
**Support URL:** https://github.com/nieuwoudtza/Cinema-Guard/issues

## Privacy tab

**Single purpose:**

> Cinema Guard mutes swear words in online videos (YouTube and other video sites), using the videos' captions, and blanks those words in the captions shown.

**Permission justifications:**

| Permission | Justification |
|---|---|
| `storage` | Keeps the user's word lists, settings, unfiltered channels and statistics in the browser. Settings use the browser's own sync storage so they follow the user to their other browsers. |
| `scripting` | Removes the per-site scripts that versions before 1.0.5 registered for sites the user turned on one by one, now that the scripts run on every site. |
| Host permissions (`https://*/*`, `http://*/*`) | Video players on film and video sites are often embedded from other sites, so Cinema Guard has to run wherever a video plays to find its captions. It reads the video's subtitles, mutes the video at the listed words and blanks them in the captions on screen. The user chooses where it filters (everywhere except a blacklist, or only on a whitelist); subtitles are read everywhere so a site starts filtering the moment it is allowed. Pages without a video are left alone, and nothing is ever sent anywhere. |

**Remote code:** No, I am not using remote code. All code is in the package.

**Data usage:** tick nothing. Cinema Guard does not collect or transmit any user data. Captions, word lists, settings and
statistics stay in the browser; settings sync only through the browser's own sync. Then tick all three certifications
(not sold to third parties; not used or transferred for purposes unrelated to the single purpose; not used to determine
creditworthiness or for lending).

**Privacy policy URL:** https://wavebit.dev/software/cinemaguard/privacy

The page lives in the WaveBit Website project (`wwwroot/docs/software/cinemaguard-privacy.md`, served by
`SoftwareController.Privacy`). It covers the extension as below and the Windows app as well.

## Privacy policy text (the extension's part)

> **Cinema Guard browser extension: privacy policy**
>
> Cinema Guard does not collect, store on any server, sell or share any personal information.
>
> To find the words to mute, it reads the captions of the video you are watching, inside your browser. On YouTube it asks YouTube for the video's caption file, the same file YouTube's own player uses. Nothing is sent to WaveBit or anyone else.
>
> Your word lists and settings are kept in your browser's storage. If you use your browser's sync, your browser syncs them to your other browsers through your browser account; WaveBit never sees them. Statistics (how many words were muted, and in which videos) stay on your computer and can be cleared at any time from the extension's Statistics page.
>
> Cinema Guard runs on the web pages you visit so it can find videos and their captions, which may be embedded from other sites. On pages without a video it does nothing. You choose where it filters: everywhere except the sites on your blacklist, or only the sites on your whitelist.
>
> Questions: https://github.com/nieuwoudtza/Cinema-Guard/issues

## Distribution tab

Visibility **Public**, all regions, free.
