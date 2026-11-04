# Chrome Web Store listing: Cinema Guard

Everything the developer dashboard asks for, ready to paste. Images are in this folder.

## Store listing tab

**Name:** Cinema Guard

**Summary** (132 characters at most):

> Mutes swearing in YouTube videos as it happens, using their captions. Choose your own words; nothing leaves your browser.

**Category:** Entertainment

**Language:** English

**Description:**

> Cinema Guard mutes swearing in YouTube videos while you watch, so the whole family can enjoy them without the language.
>
> It reads a video's captions ahead of time, finds the words on your list and mutes just those moments. With YouTube's auto-generated captions every word has its own time, so only the word itself goes quiet. The sound fades out and back in over a few milliseconds, so a mute never clicks.
>
> WHAT IT DOES
> • Mutes only the words you choose: tick whole word families, pick single spellings, or add your own words and phrases.
> • Two kinds of mute: the word only (everyday swearing), or the whole sentence (blasphemy and insults).
> • Blanks the filtered words in the captions on screen too, with a character of your choice.
> • Marks on YouTube's progress bar show where a video will be muted.
> • Works on videos, Shorts, and YouTube videos embedded on other sites.
> • Leave channels you trust unfiltered, or pause it on a site.
> • See what was found in the video you're watching, and jump straight to any of it.
> • Statistics: how many words were muted, which ones, and in which videos.
> • Alt+Shift+M turns filtering on or off from any tab.
> • Your settings follow you to your other browsers through your browser's own sync.
>
> OTHER SITES
> Cinema Guard can also run on other sites with captions or subtitles: open a video there, select Cinema Guard in the toolbar and choose Turn on. It reads the video's subtitle tracks, the subtitle files the player downloads, or the captions as they appear on screen.
>
> HELP WANTED: NETFLIX, PRIME VIDEO, DISNEY+, MAX AND HULU
> Cinema Guard knows how these services deliver their subtitles, but they need paid accounts and nobody has been able to test them yet, so they are off by default. If you have one of these services and would like to help, open a video there, select Cinema Guard in the toolbar, choose "Try it anyway", and tell us how it went: https://github.com/nieuwoudtza/Cinema-Guard/issues
>
> PRIVATE BY DESIGN
> No account, no tracking, no data sent anywhere. Captions are read and matched inside your browser.
>
> FOR YOUR OWN FILMS
> Cinema Guard for Windows cleans your own video files the same way, with muted words and clean subtitles in a new copy: https://wavebit.dev/software/cinemaguard

**Images:**

| Field | File |
|---|---|
| Store icon (128 × 128) | `extension/icons/icon-128.png` |
| Screenshots (1280 × 800) | `screenshot-1-popup.png` … `screenshot-5-sites.png`, in that order |
| Small promo tile (440 × 280) | `promo-small-440x280.png` |

**Official URL:** none (it needs a verified domain in Search Console; optional).
**Homepage URL:** https://wavebit.dev/software/cinemaguard
**Support URL:** https://github.com/nieuwoudtza/Cinema-Guard/issues

## Privacy tab

**Single purpose:**

> Cinema Guard mutes swear words in online videos (YouTube, and other sites the user turns on), using the videos' captions, and blanks those words in the captions shown.

**Permission justifications:**

| Permission | Justification |
|---|---|
| `storage` | Keeps the user's word lists, settings, unfiltered channels and statistics in the browser. Settings use the browser's own sync storage so they follow the user to their other browsers. |
| `scripting` | Runs Cinema Guard on sites outside YouTube that the user turns on themselves from the toolbar popup. Scripts are registered only for those sites. |
| `activeTab` | Lets the toolbar popup see the address of the tab it was opened on, to show whether Cinema Guard runs there and offer to turn it on. |
| Host permission: `youtube.com`, `youtube-nocookie.com` | Reads a video's captions to find the words to mute, mutes the video at those moments, blanks the words in the captions on screen, and marks the progress bar. Embedded YouTube players on other sites are covered by `youtube-nocookie.com` and `youtube.com/embed`. |
| Optional host permissions (`https://*/*`, `http://*/*`) | Asked for one site at a time, only when the user chooses Turn on (or Try it anyway) in the popup on that site, so Cinema Guard can read its captions and mute its videos. Never requested otherwise. |

**Remote code:** No, I am not using remote code. All code is in the package.

**Data usage:** tick nothing. Cinema Guard does not collect or transmit any user data. Captions, word lists, settings and
statistics stay in the browser; settings sync only through the browser's own sync. Then tick all three certifications
(not sold to third parties; not used or transferred for purposes unrelated to the single purpose; not used to determine
creditworthiness or for lending).

**Privacy policy URL:** publish the text below (for example at `https://wavebit.dev/software/cinemaguard/privacy`) and
enter that address.

## Privacy policy text

> **Cinema Guard browser extension: privacy policy**
>
> Cinema Guard does not collect, store on any server, sell or share any personal information.
>
> To find the words to mute, it reads the captions of the video you are watching, inside your browser. On YouTube it asks YouTube for the video's caption file, the same file YouTube's own player uses. Nothing is sent to WaveBit or anyone else.
>
> Your word lists and settings are kept in your browser's storage. If you use your browser's sync, your browser syncs them to your other browsers through your browser account; WaveBit never sees them. Statistics (how many words were muted, and in which videos) stay on your computer and can be cleared at any time from the extension's Statistics page.
>
> Cinema Guard runs on YouTube, and on other sites only after you turn it on for them yourself.
>
> Questions: https://github.com/nieuwoudtza/Cinema-Guard/issues

## Distribution tab

Visibility **Public**, all regions, free.
