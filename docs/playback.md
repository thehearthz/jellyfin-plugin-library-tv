# Playback, encoding and other apps

How programs are played, encoded and recovered, and how to watch in other apps.

[← Back to the README](../README.md)

## Watching in other apps (M3U)

Under **Server & export → Watch in other apps (M3U)**, turn on **Let other apps play these channels with a link**, save, and copy the two links:

- **Playlist (M3U)**: `…/LibraryTV/playlist.m3u?token=…`, the channel list with numbers and names.
- **Guide (XMLTV)**: `…/LibraryTV/epg.xml?token=…`, the same guide Jellyfin shows. The playlist also names it, so many apps find it by themselves.

Add them to an IPTV app such as VLC, TiviMate or Kodi's IPTV Simple Client. Each channel plays as MPEG-TS with H.264 video and stereo AAC sound, joining the current program like any other viewer.

Things to know:

- These apps cannot sign in to Jellyfin, so **anyone who has a link can watch**. It is off by default; treat the links like a password. **Make new links** (then save) cuts off everyone using the old ones.
- Links use the address you opened the settings page with. Open the page from the address your other devices use (not `localhost`) before copying them.
- These streams come straight from Library TV, not through Jellyfin's transcoder, so there is no per-device transcoding or bitrate limit, and Jellyfin's user permissions and parental controls do not apply.
- Every app watching counts as a viewer; channels in use count toward **Maximum channels streaming at once**.
- The export has been tested with FFmpeg and automated tests, not with every IPTV app.

## Watch from the start

The **Library TV** folder on the home screen has a **Watch from the start** folder. It lists what each channel is showing now (during a break, the program the break interrupts). Playing one plays that episode or movie's own file from its beginning, through Jellyfin's normal playback and transcoding. The channel itself carries on live. Only programs a channel aired in the last few hours can be opened this way.

## The guide

Guide entries carry what your library knows about each program: the show and episode name, season and episode number, description, genres, rating, year and picture. Live TV's **Movies**, **Shows**, **Kids**, **News** and **Sports** sections use the genres (Kids covers Kids, Children and Family; Sports and News match genres containing those words). Programs are marked as repeats, so the guide doesn't badge them as new. The XMLTV export carries the same details for IPTV apps.

## Volume

**Even out volume** (Playback tab) levels loudness to broadcast TV's standard (−24 LUFS, ATSC A/85) so commercials aren't louder or quieter than the shows around them.

- **Commercials and bumpers** (the default) levels only clips. **Everything** levels the whole channel, programs too. **Off** leaves sound untouched.
- **It works in every app.** Levelling happens on the server, in the stream itself, so the Jellyfin web app, TV apps, phones and IPTV players all hear the same levels with no add-on or custom app.
- **Measured ahead of time.** A background task measures each file once, at low priority (progress is under **Health → Volume measuring**). Clips are measured whole; programs over 40 minutes from three ten-minute stretches. A measured file then plays with a fixed correction, so it is level from its first second and keeps its own dynamics.
- **Never silent, never clipping.** Corrections are capped (at most +12 dB louder, −18 dB quieter), files quieter than −60 LUFS are left alone, and a limiter keeps peaks a decibel below full scale. Until a file is measured, FFmpeg levels it as it plays (the first second or two can be uneven). If levelling ever stops a program from starting, it plays without levelling and Health says so.
- Only the sound is re-encoded (to stereo AAC); the picture is still copied when it can be.

## Up next

Turn on **Show "Up next" near the end of programs** (Playback tab) for a card in the lower left that names the next program. It slides in 40 seconds before the program ends, or at the start of sped-up credits, stays for 12 seconds and fades out. The card has rounded corners, a gold accent and a dark translucent background.

| Style | What it shows |
| --- | --- |
| **Card: poster and title art** (default) | The show's or movie's poster, "UP NEXT", the title art (logo) and the episode or year |
| **Card: poster and title** | The poster with the title written out |
| **Card: title art only** | The title art without a poster |
| **One line of text** | "Up next: Title" on one line |

The poster and title art come from Jellyfin (the series' poster and logo for episodes). A program without title art has its title written instead; one without a poster gets a narrower card.

The card is drawn on the picture, so it only appears on programs that are **already being encoded**: sped-up credits, a channel logo, captions, a picture-shape change or **Always encode**. A copied program is never encoded just for the card. If drawing it fails, the program plays without it and Health says so.

## Now playing card, channel bug and card look

Also on the Playback tab, and drawn only on programs that are already being encoded, like Up next:

- **Now playing card**: one second into a program, a card with its backdrop, "NOW PLAYING", the title art (or title) and the episode or year slides in for 8 seconds. It shows on a program's first part only, not after each break.
- **Channel bug**: a lower-third with the channel's logo (its Guide image, or the watermark; the number in gold when there's neither), its name and "Now · title". It slides in for 8 seconds at every interval you choose on the local clock: every 30 minutes means :00 and :30. It skips a moment when a card is already showing.
- **Card and bug look**: **Frosted glass** (the default) blurs the picture behind the card under a translucent panel; **Solid** uses a dark panel. Both have rounded corners and a soft drop shadow. Frosting costs a little CPU, only while a card is on screen.

## Station idents

**Station ident** (Playback tab) plays a short animated ident before every program: the channel's logo (or its number in gold) rises and fades in, a gold line draws out beneath it, then the channel name and number appear, with an optional three-note chime. Choose 3, 5, 8 or 10 seconds.

- Idents are drawn by FFmpeg; there are no files to make. They're encoded in the channel's picture shape.
- They're part of the schedule (listed as "Station ident" in the guide preview), so turning them on or changing their length rebuilds the guide from the next program.

## Playback

Every channel plays files directly when it can and encodes only when it has to. For each program the plugin reads the file's streams from Jellyfin's library data (or FFprobe, if Jellyfin has none), then:

| Source | What happens |
| --- | --- |
| 8-bit H.264 video | Copied without encoding |
| Other video (HEVC, MPEG-2, MPEG-4, VC-1, 10-bit H.264, interlaced) | Encoded to H.264 at its own size, capped at 1080p |
| Stereo 48 kHz AAC audio | Copied without encoding |
| Other audio (AC-3, DTS, MP3, surround, mono, 44.1 kHz) | Encoded to stereo 48 kHz AAC, which costs little CPU |
| No audio track | A silent stereo AAC track is added |

So every program on a channel reaches Jellyfin as H.264 video with stereo AAC audio, in the channel's picture shape. A codec or audio-layout change between programs is a common reason live streams freeze at program changes, and this avoids it while encoding nothing that is already compatible. A library that is all H.264/AAC is never encoded. Channel health shows what the current program is doing, for example "Direct: video and audio copied without encoding".

**Encoding quality.** Each channel's Content tab has **Encoding quality** (Fast, Balanced, High, Best) and **Tallest encoded picture** (480p, 576p, 720p, 1080p). They apply only to programs that are encoded, never to copied ones. Balanced at 720p is what earlier versions always used (x264 ultrafast, CRF 23, 192 kbps audio); Fast uses CRF 27 and 128 kbps; High uses the veryfast preset, CRF 20 and 224 kbps; Best uses the faster preset, CRF 17 and 256 kbps. Better quality and taller pictures cost much more CPU, and Channel health reports encoding that cannot keep up. Automatic and smart channels use Balanced at 720p.

**CPU or GPU.** Under **Encoding hardware** in the plugin's settings, **Encode on** chooses where programs that have to be encoded are encoded: the **CPU** (the default, works everywhere), **NVIDIA NVENC**, **VAAPI** (Intel or AMD on Linux, with a configurable render device, `/dev/dri/renderD128` by default) or **Intel Quick Sync**. Only the H.264 encoding moves to the graphics card; decoding, scaling, captions, tone mapping and the logo stay on the CPU, which works with every driver and keeps pictures identical, and audio is still encoded on the CPU (cheaply). Programs that are copied use no encoder either way. **Test hardware encoding** tries each encoder with Jellyfin's FFmpeg and shows which ones work. If the chosen encoder does not work (no card, no driver, the device not passed into a Docker container, an FFmpeg without that encoder), programs are encoded on the CPU instead and Channel health says so, so a channel never goes dark because of a missing GPU. Each channel's Content tab has **Encode with**, which can pin a channel to the CPU. The encoding quality presets map to the card's own settings (for example NVENC presets p1 to p5 with constant quality 30 to 20). Graphics encoders have not been tested on real hardware by the author, only through FFmpeg's command line and the CPU fallback, so please report what works for you.

**Picture shape.** Jellyfin sizes the picture it sends a viewer from the first program that viewer sees, so a later program of another shape would be stretched or squashed: a 4:3 cartoon after a widescreen show, or a 2.39:1 film. Each channel therefore has a **Picture shape**, 16:9 by default. Programs within 8% of that shape are copied as before, so 1.85:1 films and widescreen DVDs play directly on a 16:9 channel. Programs in a visibly different shape are encoded into that shape with black bars, at no more than 720p to keep the encoding light: a 4:3 show gets bars at the sides (a 640×480 file becomes 854×480), and a 2.39:1 film gets bars at the top and bottom (1920×800 becomes 1280×720). Choose **Classic 4:3** for a channel of mostly older shows, so they play directly and widescreen programs get bars instead. **Each program's own shape, restarting players** plays every program in its own shape with no bars and no encoding for shape. When a program of a different shape starts, Library TV asks every app watching that channel to start it again (the same remote-control command as casting), so Jellyfin sizes the new stream for the new program. Expect a few seconds of loading at each shape change. Commercials and bumpers are fitted to the shape of the program they follow, so a break never causes a restart. The official Jellyfin apps accept these commands; apps that don't keep playing and may stretch. This has not yet been tested against every Jellyfin client. **Each program's own shape** turns all of this off and encodes nothing for shape, at the risk of stretched pictures on some players.

**Smooth program changes.** Each program, part, bumper and commercial starts its own FFmpeg process, which takes a moment to produce video. So programs run about three seconds ahead of the clock: Library TV reads the timestamps in the video and sends each piece up to three seconds before it is due, and the next program starts as soon as the one before finishes, while the player still has those seconds buffered. This pacing works the same on every FFmpeg version.

When copied video resumes after a break, it starts on the keyframe just before the cut (found with FFprobe), so it repeats a few seconds and its timestamps continue in order after the break.

If encoding cannot keep up with real time (it produces less than 95% of real time for twenty seconds), Channel health reports it, because the stream will freeze. Use a picture shape that matches most of the channel's programs, or turn off **Always encode**, so fewer programs are encoded.

Resolution and frame rate still change between programs when files differ; in the same shape this only changes sharpness. If a channel still freezes when programs change, turn on **Always encode every program** for that channel. Every program is then converted to 720p/30fps H.264/AAC, which gives the most consistent stream at much higher CPU cost. Encoding uses Jellyfin's FFmpeg, with libx264 in software or a graphics card (see CPU or GPU above). Jellyfin may additionally transcode Live TV for a particular client.

Files must be locally readable by the Jellyfin process and contain a video stream. Remote streams, DRM, images, music-only channels, subtitles and live inputs are not supported. Copy seeking is keyframe-limited, so joins may begin slightly before the exact schedule position. At file transitions the transport signals a discontinuity.

Channels saved by earlier versions switch to this behavior automatically. The earlier `Transcode` setting is no longer used; use **Always encode every program** instead.

## Captions

Each channel has a **Captions** setting on its Content tab: **Off** (the default), **Forced only** (just the forced tracks that translate foreign-language dialogue) or **On when the program has captions**, plus an optional **Caption language** such as `English`, `en` or `eng` (blank accepts any language). Captions come from subtitle tracks inside the file or subtitle files beside it that Jellyfin has found, such as `Episode.en.srt`. With **On**, full captions are preferred over forced-only tracks, then SDH or closed-caption tracks, then the file's default track. A program with no track in the chosen language plays without captions.

Live TV streams cannot carry text subtitle tracks, so captions are drawn into the picture: everyone watching the channel sees them, in every Jellyfin app, and they cannot be turned off per viewer. A program with captions is encoded while it plays (at no more than 720p), which uses more CPU; programs without captions still play directly. To offer both, duplicate the channel and turn captions on in the copy. Captions stay in sync when a program starts part-way through, after a break or when you tune in. Text tracks (SRT, ASS/SSA, WebVTT, MP4 text) are drawn with Jellyfin FFmpeg's libass; picture tracks from discs (PGS, DVD, DVB) are overlaid from inside the file. Embedded CEA-608/708 closed captions in the video itself are not supported.

## Audio language

**Preferred audio language** (also on the Content tab) picks the soundtrack when a program has several, such as an English dub over a Japanese original: type a name or code like `English`, `en` or `eng`. Commentary tracks are skipped, and among several matching tracks the file's default wins. Programs without that language, and channels with the setting blank, play the file's default soundtrack. The chosen track is copied when it is stereo 48 kHz AAC and encoded otherwise, like any other audio, and Channel health names its language.

## Channel images and stream choices

On a channel's Content tab:

- **Channel logo** is shown for the channel in Live TV. **Offline card** plays instead of black during schedule gaps longer than about eight seconds, as H.264 video with silent AAC.
- **Watermark** draws a logo (position, opacity, width) over everything on the channel, including commercials and bumpers. Because it is drawn on the picture, every program on that channel is encoded, as with captions or credits speed; Channel health says so. A missing image is reported and ignored.
- **Preferred audio track title** picks a track whose title contains the text (for example "Dolby"), among tracks in the preferred language. Without a default track, the track with the most channels is used.

Image settings are full server paths to PNG, JPG or WebP files, entered by an administrator in the builder. They are used as FFmpeg inputs and shown in Live TV, and are never accepted from non-administrators.

## Playback recovery and diagnostics

**Starting a program plainly.** If FFmpeg sends nothing at all for a program that uses extras (HDR tone mapping, graphics-card encoding, captions or the channel logo), the program is started again without them instead of being skipped, and Channel health says which extras failed and quotes FFmpeg's own message. Failures and stalls are reported with the program's name and FFmpeg's message.

**Keeping Jellyfin's guide in step.** Jellyfin keeps its own copy of the guide. Whenever programs it already lists are replaced or moved (a rebuild after changed settings, a skipped or shortened program), Library TV queues Jellyfin's **Refresh Guide** task. Until that finishes, the guide can briefly show the earlier program.

If FFmpeg sends nothing for 12 seconds (20 seconds while starting), for example because a network mount hangs, or exits partway through a program, the plugin restarts that program at the current position. Viewers stay connected, and the connection is kept busy with MPEG-TS filler packets while it restarts. A program is restarted at most twice before it is treated as unplayable. If a file ends more than five seconds before its listed runtime, the next program starts at once instead of leaving dead air.

If a watched program is missing, unreadable or fails to produce a transport stream, the plugin brings the next scheduled item forward and shifts the later guide times by the same amount. Rotation selections remain consumed once; stale recovery requests cannot skip twice. Recovery is limited to three consecutive failed items per viewing session (two advances, then stop), with no background retry loop. Successful playback resets the consecutive-failure count. If FFmpeg itself cannot start, playback stops with a configuration message and the schedule is not advanced. Because Jellyfin keeps its own copy of the guide, a recovery queues Jellyfin's **Refresh Guide** task once so the shifted times appear; it is skipped if that task is already running or queued.

Channel health shows source/schedule warnings, active viewers, recovery count, playback mode, how the current program is being played (copied or encoded) and the most recent playback issue. It does not probe media or run FFmpeg. Playback diagnostics are kept in bounded memory, not a growing history file. A refresh records its elapsed time; this is a diagnostic measurement, not a server-wide CPU benchmark.

FFmpeg is killed and awaited on cancellation, output failure or a stall. A channel keeps playing for ten seconds after its last viewer leaves (Jellyfin reads a channel briefly, disconnects, then connects again to play it, and this avoids starting the program twice); after that no media process runs. Recovery does not guarantee seamless playback on every client or reconnect clients that independently disconnect; a client whose own player stalls may need to be restarted. Real FFmpeg tests validate compatible program/commercial transitions and cleanup; real client testing is still needed.

If a pool is empty or exhausted with repeats disabled, its time block falls back to the default source. If both are unavailable, the guide has a gap rather than silently repeating episodes. Recordings are not supported by this provider.

## Deferred startup

Library TV's background workers return from host startup immediately. They wait for **both** the host's `ApplicationStarted` signal and Jellyfin's `CoreStartupHasCompleted` flag, then share a **30-second settling delay** before initializing the scheduler or registering the Plugin Pages entry. Library scanning, guide loading/generation, schedule writes and heavy service resolution are deferred. Early calls to the channel providers return no content without resolving library/user services. No FFmpeg process starts automatically.

The wait uses a startup notification, a one-second asynchronous readiness check only while core startup is unfinished, and one settling timer shared by both workers. It ends after readiness and cancels if the server shuts down. There is no blocking sleep and no ongoing startup polling after the wait finishes.

During this brief period the builder reports that Library TV is waiting and offers **Retry loading builder**. Manual library/refresh requests receive HTTP 503 with a retry hint, so they cannot bypass the startup gate. Channels and the Plugin Pages entry become available after the wait and the first schedule refresh. Refresh the web client; if a Jellyfin guide/channel import ran earlier, run **Refresh Channels** and **Refresh Guide** after Library TV is ready.

Jellyfin still discovers the plugin assembly and registers its lightweight services during normal plugin discovery. This does not force assembly loading to be last, nor guarantee that every third-party plugin has finished arbitrary background work. The readiness signals and settling delay keep Library TV's heavy work outside Jellyfin's core startup path. Real-server startup timings have not been benchmarked.

## Server networking

`InternalBaseUrl` is the Jellyfin URL reachable **from its own process**, normally `http://127.0.0.1:8096`. Include a configured Jellyfin base path, for example `http://127.0.0.1:8096/jellyfin`. Docker uses the container's port, not the host's mapped port. Loopback HTTPS requires a trusted certificate. The transport endpoint only accepts loopback connections with a private random token; users tune through Jellyfin's normal authorized Live TV endpoints. Administrative configuration and library discovery require the administrator policy.

The private transport token is stored in the plugin configuration and can appear in server diagnostics. Treat configuration and logs as private. Stream sources are exclusively drawn from local indexed library items; there is no shell command construction or arbitrary path API.
