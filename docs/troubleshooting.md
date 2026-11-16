# Troubleshooting

What Library TV's messages mean and what to do about them. Start with **Health** in the settings page: it shows each channel's problems, what is playing and how it is being played.

[← Back to the README](../README.md)

## Something isn't right

| What you see | Likely cause | What to do |
| --- | --- | --- |
| Channels don't appear in Live TV | Jellyfin hasn't read them yet | Run **Refresh Channels** and **Refresh Guide** under Dashboard → Scheduled Tasks |
| The guide shows one thing, another plays | Jellyfin's copy of the guide is older than the schedule | Library TV asks Jellyfin to re-read the guide when programs change; wait a minute, or run **Refresh Guide** |
| Tuning in loads for a long time | A program had to be encoded and the server is slow, or the file failed to start | Check Health for "could not start" or "too slow" messages below |
| The picture freezes every few seconds | Encoding can't keep up with real time | See "Encoding runs at …× real time" below |
| Commercials are much louder than shows | Volume levelling is off | Playback tab → **Even out volume: Commercials and bumpers** |
| "Up next" never appears | The program isn't being encoded, or the option is off | See [Up next](playback.md#up-next): the card only shows on programs already being encoded |
| An IPTV app can't play the M3U | Export is off, the link is old, or the address isn't reachable | Turn export on, copy fresh links from the address your devices use |
| Credits aren't sped up | No credits markers yet | See "None of the upcoming programs have credits markers" below |

## Health messages

### Playback

| Message | What it means | What to do |
| --- | --- | --- |
| This program could not start with *…*, so it is playing without. FFmpeg said: *…* | An extra (graphics-card encoding, HDR tone mapping, captions, the channel logo, "Up next" or volume levelling) made FFmpeg fail, so the program plays without it | Read FFmpeg's message. For the graphics card, use **Test hardware encoding**; for the logo, check the image file; for captions, try another caption setting |
| Encoding runs at *N*× real time, too slow to keep up, so the stream freezes | The server can't encode this program fast enough | Lower **Encoding quality** or **Tallest encoded picture**, turn off **Always encode**, choose another picture shape, or use a graphics card |
| A scheduled file is missing | The file was moved, deleted or its drive is offline | Check the library and its storage; run a library scan |
| A scheduled item could not play | FFmpeg couldn't read the file | Check the file plays in Jellyfin; **Always encode** may help with unusual formats |
| A scheduled item kept stalling and was skipped | Reading the file kept stopping, often a slow or sleeping network drive | Check the storage and network |
| *…* during "*title*" and restarted automatically | The program stalled once and was restarted where it was | Nothing, unless it repeats for the same file |
| Viewer could not keep up with the live stream | One viewer's connection was too slow and was dropped; others carry on | Check that viewer's network, or lower the encoding settings |
| Jellyfin FFmpeg could not start | Jellyfin's FFmpeg path is wrong or FFmpeg is missing | Check Jellyfin's FFmpeg path (Dashboard → Playback → Transcoding, or the `--ffmpeg` / `JELLYFIN_FFMPEG` startup setting) |
| The *channel logo* / *offline card* image was not found at *path* | The image setting points to a missing file | Fix the path in the channel's Playback tab |

### Schedule

| Message | What it means | What to do |
| --- | --- | --- |
| No programs are queued | Nothing matched the channel's content | Check the content selection, its filters (including **Skip what this person has watched**), time blocks and missing files |
| The default lineup has no eligible local videos | The channel's main content is empty | Time outside blocks may be empty; add content on the Content tab |
| The *start–end* block has no eligible local videos | A block's content is empty | The block falls back to the default lineup; fix its content or filters |
| The *start–end* block waits for its exact start or fills to its end, but … the time before it is dead air | An exact or fit block has nothing to fill gaps with | Add commercials or bumpers, or use a soft start |
| Some times have nothing allowed to air | Content restrictions leave nothing allowed at some hours | Add content or narrow the restrictions |
| This channel reached the 10,000-program guide limit | Very short clips with a long guide | Use longer clips or fewer guide days |

### Commercials, bumpers and credits

| Message | What it means | What to do |
| --- | --- | --- |
| Commercial breaks are enabled but no readable commercial clips were found | The commercial selection is empty or unreadable | Choose a commercial folder, or use the shared commercial library |
| Commercials are available, but Clips to play in each break is 0 | Breaks are set to zero clips | Raise **Clips to play in each break** |
| No commercials fit this channel's programs | Every clip is named after a network or show that doesn't match | Add general commercials without a network in the name, or check networks in **Guide preview** |
| Only *N* different commercials fit this channel's programs | Few matching clips, so they repeat | Add more commercials, especially general ones |
| Bumpers are selected but no readable bumper clips were found / No bumpers fit | As for commercials | As for commercials |
| Padding is on, but there are no commercials or bumpers to fill the time | Round-time padding has nothing to pad with | Add clips or turn padding off |
| None of the upcoming programs have credits markers | Credits can't be sped up without knowing where they start | Wait for credits detection (progress in Health), or add credits chapters or Outro segments (for example with Intro Skipper) |

## Reporting a problem

[Open an issue](https://github.com/thehearthz/jellyfin-plugin-library-tv/issues) with:

- the Health text for the channel (it names the program and quotes FFmpeg),
- the client app you watch with (web, Android TV, Roku, an IPTV app…),
- the relevant part of Jellyfin's log (Dashboard → Logs), searching for "Library TV".
