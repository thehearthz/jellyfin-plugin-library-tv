# Library TV for Jellyfin 12.2

Always-on TV channels built from the shows and movies already in your Jellyfin library. Tune in and join whatever is "on", with commercials, bumpers and a guide, like broadcast TV.

**Experimental.** It passes automated scheduling, UI and real-FFmpeg tests, but it has not been tested on every Jellyfin client. Start with one small channel.

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:

   ```text
   https://raw.githubusercontent.com/thehearthz/jellyfin-plugin-library-tv/main/manifest.json
   ```

2. Install **Library TV** from the catalog and restart Jellyfin.
3. Open **Dashboard → Plugins → Library TV**. Under **Server & export**, set your time zone (for example `America/New_York`).
4. Under **Channels**, click **New channel → Choose shows & movies**, pick content, then **Save channels**.
5. Run Jellyfin's **Refresh Channels** and **Refresh Guide** tasks. Channels appear in a **Library TV** folder on the home screen and under **Live TV**.

Updating: install the new version and restart once. Channels, numbers and episode progress are kept.

## What you get

- **Channels your way.** Hand-picked channels, one automatic channel per library, or smart channels built from genres, decades, years and collections.
- **A real schedule.** Time blocks by day and hour, exact or soft starts, programs that start on the hour or half hour, and episodes that always continue in order.
- **Commercials and bumpers.** Breaks between and inside programs, matched to each program's network and era, from your own clip folders.
- **Credits.** Finds where credits start and can speed through them.
- **Light on the server.** H.264/AAC files play directly; only other formats are encoded, on the CPU or a graphics card (NVENC, VAAPI, Quick Sync), and only while someone is watching.
- **Looks right.** Consistent picture shape, HDR tone-mapped to SDR, optional burned-in captions, audio language choice, channel logo and watermark.
- **Sounds right.** Clips, or the whole channel, levelled to broadcast loudness on the server, measured ahead so it's even from the first second, in every app.
- **On-screen graphics.** Up next and Now playing cards with posters and title art, a lower-third channel bug, and animated station idents, with frosted glass and drop shadows.
- **Seasonal programming.** Blocks that run only between dates, such as holiday episodes every December.
- **Fresh picks.** Channels can skip what a chosen person has already watched.
- **Watch from the start.** Play what's on now from its beginning, while the channel carries on.
- **Guide.** A rolling 1–7 day guide in Jellyfin's Live TV with posters, episodes, genres and ratings, kept in step when the schedule changes.
- **Other apps.** An optional M3U playlist and XMLTV guide for IPTV players such as VLC, TiviMate or Kodi.
- **Safe to run.** Channel health explains problems, playback recovers from stalled or missing files, and settings can be backed up and restored.

## The settings page

| Section | What it is for |
| --- | --- |
| **Channels** | Build each channel: Content, Schedule, Commercials, Bumpers, Playback, Guide preview |
| **Automatic & smart** | A channel per library, and channels from genres, years and decades |
| **Commercial library** | Shared commercials and bumpers, how they were sorted, and fixing clips |
| **Restrictions** | Keep genres or tags off the air during set hours |
| **Health** | What each channel is doing, problems, and credits detection progress |
| **Server & export** | Time zone, guide length, encoding hardware, M3U links, backups |

## Good to know

- **Guide accuracy.** Jellyfin keeps its own copy of the guide. Library TV asks Jellyfin to re-read it whenever programs change, but for a short time it can show the earlier program.
- **Captions are burned in.** Live streams cannot carry text subtitles, so everyone watching a channel sees them. Duplicate a channel to offer both.
- **M3U links work without signing in.** Anyone who has a link can watch. It is off by default; treat links like a password.
- **Local files only.** No remote streams, DRM, music-only channels or recordings.
- **Graphics-card encoding** has not been tested on real hardware by the author. Use **Test hardware encoding**; if it does not work, the CPU is used.

## Documentation

- [Building channels](docs/channels.md): content, time blocks, seasonal blocks, timing, filters, skipping watched, smart channels, collections, restrictions, channel numbers.
- [Commercials, bumpers and credits](docs/commercials.md): breaks, the shared clip library, network and era matching, credits.
- [Playback, encoding and other apps](docs/playback.md): watch from the start, guide details, volume, Up next, what is copied or encoded, quality, CPU or GPU, picture shape, captions, M3U export, recovery.
- [Troubleshooting](docs/troubleshooting.md): what each Health message means and what to do.
- [Administration and development](docs/admin.md): Plugin Pages navigation, backups, building and releasing.

## Problems?

Open **Health** in the settings page first: it names the program that failed and quotes FFmpeg's message. [Troubleshooting](docs/troubleshooting.md) explains each message. Then [open an issue](https://github.com/thehearthz/jellyfin-plugin-library-tv/issues) with that text, your client app and Jellyfin's log.

License: GPL-3.0-only.
