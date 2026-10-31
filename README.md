# Library TV for Jellyfin 12.2

Build always-on television channels from local media already indexed by Jellyfin. **Experimental v0.4** bundles the visual builder, custom numbering, deferred startup, playback recovery, future-slot rebuilding, diagnostics and settings backups. It compiles against the official 12.2 API and passes automated scheduling, integration, UI and real FFmpeg transport tests. Real Jellyfin client compatibility still needs validation; start with a small channel.

This is a cumulative release. You can skip earlier versions and install the latest version from the catalog in one update/restart when ready. You do not need to restart the server for subsequent channel-setting changes, guide rebuilds, or settings restores.

## Install

1. In Jellyfin **Dashboard → Plugins → Repositories**, add a repository named **Library TV** with this URL:

   ```text
   https://raw.githubusercontent.com/thehearthz/jellyfin-plugin-library-tv/main/manifest.json
   ```

2. Open the plugin catalog, install **Library TV**, then restart Jellyfin.
3. Open **Dashboard → Plugins → Library TV**. Set your schedule time zone, such as `America/New_York`.
4. Click **New channel → Choose shows & movies**. Browse or search your library, select your shows and movies, click **Done**, then **Save channels**. Automatic whole-library channels are also available in the **Automatic channels** section.
5. Run Jellyfin's **Refresh Channels** scheduled task. A separate **Library TV** folder appears under **My Media** on the home screen, alongside your movie and show libraries. Open it and play a channel to join its current program. You do not need to create a filesystem library or add `.strm` files.
6. Run Jellyfin's **Refresh Guide** task to also show channels and schedules under **Live TV**. Give viewers normal Jellyfin Live TV access.

Upgrading from earlier versions: install the latest version and restart Jellyfin once when convenient. Existing channel IDs and saved episode rotations are preserved. Previous automatic channel numbers are retained on the first refresh when available, then saved permanently. If older channels share a number, the builder asks you to resolve the conflict. Run **Refresh Channels** and **Refresh Guide** after changing channel numbers or names so Jellyfin imports them. If your home screen is customized, enable **Library TV** in the user's home-screen settings and refresh the client. Some clients group plugin channel folders differently; placement depends on the client.

### Plugin Pages integration

To reach the builder from Jellyfin's main web navigation:

1. Add the upstream repository `https://www.iamparadox.dev/jellyfin/plugins/manifest.json` under **Dashboard → Plugins → Repositories**.
2. Install **File Transformation** and **Plugin Pages 3.0+**, using builds compatible with Jellyfin 12.2, then restart Jellyfin.
3. Refresh the web client. Open **Library TV Builder** in the **Plugin Settings** menu section provided by Plugin Pages.

Library TV registers the page automatically through the [documented Plugin Pages v3 interface](https://github.com/IAmParadox27/jellyfin-plugin-pages#adding-your-pages). No manual JSON or web-file edits are needed. Its connection status appears under **Server settings & Plugin Pages** in the builder. The menu entry can be visible to all signed-in users, but only administrators can browse media or edit channels; other users receive an administrator-access message.

Plugin Pages and File Transformation are optional navigation dependencies and are not bundled in Library TV. The same full builder always remains available at **Dashboard → Plugins → Library TV**. The integration is for Jellyfin Web and compatible web-wrapper clients; native clients may not expose Plugin Pages navigation. The integration is tested against upstream's reflection contract, not a running installation of the companion plugins.

Jellyfin's plugin catalog and repository configuration are documented [here](https://jellyfin.org/docs/general/server/plugins/). Release ZIPs contain only the plugin DLL; Jellyfin supplies its own dependencies. Manual installation: extract the DLL into a `Library TV` directory under your Jellyfin plugins directory and restart.

## What it does

- A separate **Library TV** home folder, with one playable live item per enabled channel, alongside the existing Live TV guide integration.
- Manual channels and automatically generated channels for explicitly selected libraries.
- Custom channel numbers such as **42**, **7.2**, or **7-10**, with conflict detection and numeric subchannel ordering in the builder/provider listings. Individual clients may apply their own display ordering.
- Automatic channels have custom names and numbers, a configurable starting number for new assignments, and stable assignments when other channels or libraries change.
- Temporarily disabling a manual channel preserves its saved schedule and episode rotation; it is hidden from the published lineup until re-enabled.
- Visual show/movie picker with search, library filters, artwork, pagination, selected-content chips, and no ID copying or JSON editing.
- Channel duplication, day/time block forms, commercial-folder selection, genre/tag filters, saved-guide preview, and Plugin Pages navigation.
- Rolling 1–7 day schedule, extended periodically from current library metadata.
- Saved rotation state survives restarts. Episodes within each series follow season/episode order, even with shuffle enabled.
- A show always moves to its next episode on a channel. When the same show is in the default lineup and one or more time blocks, they share one episode position instead of each starting from episode 1. The same show held in two libraries (matched by TVDB, TMDB or IMDb ID) counts as one show, and a duplicated episode or file airs once.
- Shows take turns: by default two episodes of one show air, then the next show takes a turn, so a time block mixes its shows instead of filling with one.
- A pool airs all its selected content before cycling. New items enter the next unplanned portion of the rotation. Disable `RepeatWhenExhausted` to stop a pool when it runs out.
- Daily/weekly time blocks, local time zones, overnight blocks, and per-block daily episode limits.
- Commercial breaks from indexed library folders: one break in the middle of each episode, two during each movie (at chapter marks when available), and one after every program. Selected commercials are excluded from that channel's main content pools.
- Bumpers ("We'll be right back") play going into and coming out of each break.
- A shared commercial library is sorted automatically by the year, decade and network in each file name, after startup and at each refresh.
- Every program is shown in the channel's picture shape (16:9 by default), so a 4:3 show after a widescreen one is never stretched.
- Commercials match the network and era of the program they follow: an Adult Swim show gets Adult Swim commercials, and an episode from 1987 gets 1980s ones (see [Network-matched commercials](#network-matched-commercials) and [Era-matched commercials](#era-matched-commercials)).
- Content restrictions keep genres or tags, such as Horror, off the air during set hours on every channel.
- One FFmpeg process per actively watched channel shared by viewers. No FFmpeg processes when channels are idle. Bounded viewer queues and a simultaneous channel limit keep resource use controlled.
- H.264/AAC files play directly; only files in other formats are encoded, and only while watched. Stalled playback restarts automatically without disconnecting viewers.
- Identical content pools share their library query during each refresh. The cache ends with that refresh, so new files can appear at the next refresh without a persistent media cache. Each media folder is checked once per refresh rather than every file, which skips an offline mount cheaply; files deleted between Jellyfin library scans are skipped by playback recovery.
- Current-program lookup uses a per-channel index. State is copied without a JSON encode/decode round trip; JSON is used for durable storage. Guide previews return 40 programs by default (maximum 200 per request).
- **Channel health** checks the saved schedule only when requested and reports channels with no upcoming content without starting playback or polling in the background.
- Bounded playback recovery restarts a stalled program and advances to the next scheduled item when a media file is missing or unreadable. An unavailable FFmpeg configuration stops playback without consuming episodes.
- **Rebuild future slots** applies saved changes after the current program without resetting episode order or daily quotas. Legacy entries without rewind metadata are explicitly protected.
- Reviewed settings backup/restore excludes private stream tokens and the server connection address.
- Viewers join the program scheduled for the current time. The schedule continues while nobody watches.

## Low CPU and playback constraints

### Deferred startup

Library TV's background workers return from host startup immediately. They wait for **both** the host's `ApplicationStarted` signal and Jellyfin's `CoreStartupHasCompleted` flag, then share a **30-second settling delay** before initializing the scheduler or registering the Plugin Pages entry. Library scanning, guide loading/generation, schedule writes and heavy service resolution are deferred. Early calls to the channel providers return no content without resolving library/user services. No FFmpeg process starts automatically.

The wait uses a startup notification, a one-second asynchronous readiness check only while core startup is unfinished, and one settling timer shared by both workers. It ends after readiness and cancels if the server shuts down. There is no blocking sleep and no ongoing startup polling after the wait finishes.

During this brief period the builder reports that Library TV is waiting and offers **Retry loading builder**. Manual library/refresh requests receive HTTP 503 with a retry hint, so they cannot bypass the startup gate. Channels and the Plugin Pages entry become available after the wait and the first schedule refresh. Refresh the web client; if a Jellyfin guide/channel import ran earlier, run **Refresh Channels** and **Refresh Guide** after Library TV is ready.

Jellyfin still discovers the plugin assembly and registers its lightweight services during normal plugin discovery. This does not force assembly loading to be last, nor guarantee that every third-party plugin has finished arbitrary background work. The readiness signals and settling delay keep Library TV's heavy work outside Jellyfin's core startup path. Real-server startup timings have not been benchmarked.

### Playback

Every channel plays files directly when it can and encodes only when it has to. For each program the plugin reads the file's streams from Jellyfin's library data (or FFprobe, if Jellyfin has none), then:

| Source | What happens |
| --- | --- |
| 8-bit H.264 video | Copied without encoding |
| Other video (HEVC, MPEG-2, MPEG-4, VC-1, 10-bit H.264, interlaced) | Encoded to H.264 at its own size, capped at 1080p |
| Stereo 48 kHz AAC audio | Copied without encoding |
| Other audio (AC-3, DTS, MP3, surround, mono, 44.1 kHz) | Encoded to stereo 48 kHz AAC, which costs little CPU |
| No audio track | A silent stereo AAC track is added |

So every program on a channel reaches Jellyfin as H.264 video with stereo AAC audio, in the channel's picture shape. A codec or audio-layout change between programs is a common reason live streams freeze at program changes, and this avoids it while encoding nothing that is already compatible. A library that is all H.264/AAC is never encoded. Channel health shows what the current program is doing, for example "Direct: video and audio copied without encoding".

**Picture shape.** Jellyfin sizes the picture it sends a viewer from the first program that viewer sees, so a later program of another shape would be stretched or squashed: a 4:3 cartoon after a widescreen show, or a 2.39:1 film. Each channel therefore has a **Picture shape**, 16:9 by default. Programs within 8% of that shape are copied as before, so 1.85:1 films and widescreen DVDs play directly on a 16:9 channel. Programs in a visibly different shape are encoded into that shape with black bars, at no more than 720p to keep the encoding light: a 4:3 show gets bars at the sides (a 640×480 file becomes 854×480), and a 2.39:1 film gets bars at the top and bottom (1920×800 becomes 1280×720). Choose **Classic 4:3** for a channel of mostly older shows, so they play directly and widescreen programs get bars instead. **Each program's own shape, restarting players** plays every program in its own shape with no bars and no encoding for shape. When a program of a different shape starts, Library TV asks every app watching that channel to start it again (the same remote-control command as casting), so Jellyfin sizes the new stream for the new program. Expect a few seconds of loading at each shape change. Commercials and bumpers are fitted to the shape of the program they follow, so a break never causes a restart. The official Jellyfin apps accept these commands; apps that don't keep playing and may stretch. This has not yet been tested against every Jellyfin client. **Each program's own shape** turns all of this off and encodes nothing for shape, at the risk of stretched pictures on some players.

**Smooth program changes.** Each program, part, bumper and commercial starts its own FFmpeg process, which takes a moment to produce video. So programs run about three seconds ahead of the clock: Library TV reads the timestamps in the video and sends each piece up to three seconds before it is due, and the next program starts as soon as the one before finishes, while the player still has those seconds buffered. This pacing works the same on every FFmpeg version.

When copied video resumes after a break, it starts on the keyframe just before the cut (found with FFprobe), so it repeats a few seconds and its timestamps continue in order after the break.

If encoding cannot keep up with real time (it produces less than 95% of real time for twenty seconds), Channel health reports it, because the stream will freeze. Use a picture shape that matches most of the channel's programs, or turn off **Always encode**, so fewer programs are encoded.

Resolution and frame rate still change between programs when files differ; in the same shape this only changes sharpness. If a channel still freezes when programs change, turn on **Always encode every program** for that channel. Every program is then converted to 720p/30fps H.264/AAC, which gives the most consistent stream at much higher CPU cost. Encoding uses Jellyfin's FFmpeg with libx264 in software; hardware acceleration for the plugin's own encoding is not implemented. Jellyfin may additionally transcode Live TV for a particular client.

Files must be locally readable by the Jellyfin process and contain a video stream. Remote streams, DRM, images, music-only channels, subtitles and live inputs are not supported. Copy seeking is keyframe-limited, so joins may begin slightly before the exact schedule position. At file transitions the transport signals a discontinuity.

Channels saved by earlier versions switch to this behavior automatically. The earlier `Transcode` setting is no longer used; use **Always encode every program** instead.

### Captions

Each channel has a **Captions** setting on its Content tab: **Off** (the default), **Forced only** (just the forced tracks that translate foreign-language dialogue) or **On when the program has captions**, plus an optional **Caption language** such as `English`, `en` or `eng` (blank accepts any language). Captions come from subtitle tracks inside the file or subtitle files beside it that Jellyfin has found, such as `Episode.en.srt`. With **On**, full captions are preferred over forced-only tracks, then SDH or closed-caption tracks, then the file's default track. A program with no track in the chosen language plays without captions.

Live TV streams cannot carry text subtitle tracks, so captions are drawn into the picture: everyone watching the channel sees them, in every Jellyfin app, and they cannot be turned off per viewer. A program with captions is encoded while it plays (at no more than 720p), which uses more CPU; programs without captions still play directly. To offer both, duplicate the channel and turn captions on in the copy. Captions stay in sync when a program starts part-way through, after a break or when you tune in. Text tracks (SRT, ASS/SSA, WebVTT, MP4 text) are drawn with Jellyfin FFmpeg's libass; picture tracks from discs (PGS, DVD, DVB) are overlaid from inside the file. Embedded CEA-608/708 closed captions in the video itself are not supported.

### Audio language

**Preferred audio language** (also on the Content tab) picks the soundtrack when a program has several, such as an English dub over a Japanese original: type a name or code like `English`, `en` or `eng`. Commentary tracks are skipped, and among several matching tracks the file's default wins. Programs without that language, and channels with the setting blank, play the file's default soundtrack. The chosen track is copied when it is stereo 48 kHz AAC and encoded otherwise, like any other audio, and Channel health names its language.

## Build an evening channel

1. Click **New channel**, enter a name and number, then open **Content → Choose shows & movies**.
2. In **Movies**, choose movies for the default lineup. Use the search box and library filter to narrow the list. Click **Done**. These movies fill the time outside your scheduled blocks.
3. Open **Schedule → Add time block**. Choose 18:00–23:00, select the days, and set **Programs per day** to 3.
4. In that block's content picker, select a show. Disable shuffle if you want series in the order selected. Episode order inside each show is preserved with either setting. Select several shows and set **Episodes in a row** (2 by default) to rotate between them.
5. Open **Commercials**, set two clips per break, and select an indexed commercial folder. Add the folder to a Jellyfin video library first; the picker does not scan arbitrary filesystem paths. Name subfolders by network and decade, such as `Nickelodeon/1990s` and `Adult Swim/2000s`, so each program gets commercials from its own network and era.
6. Click **Save channels**. The **Guide preview** tab shows the saved schedule; it does not simulate unsaved edits.

This schedules three episodes during the evening block, then returns to movies. Episode rotation is independent for each channel and each block: day one 1/2/3, day two 4/5/6, and so on. The default source must provide enough repeatable content to keep the rest of the day full. Selections can include specific shows, movies, indexed folders, or entire libraries. Shows automatically include their available episodes.

### Commercial breaks and bumpers

**Clips to play in each break** sets how many commercials each break has. A break plays after every program, and **Breaks during each episode** (1 by default) and **Breaks during each movie** (2 by default) add breaks inside programs, 0–5 each. A program is split evenly, or at the chapter mark nearest each split when the file has chapters (DVD and Blu-ray rips often mark act breaks). Programs shorter than ten minutes are never split, and nothing is split while **Clips to play in each break** is 0. Each part continues exactly where the last one stopped; the Live TV guide still lists the program once. When video is copied rather than encoded, a later part starts on the keyframe just before the cut and repeats those few seconds, because copied video can only start on a keyframe; the program's final seconds are shortened by the same amount.

Select clips under the **Bumpers** tab to add bumpers. One plays going into each break and one coming out of it; on a channel without commercials, one bumper plays between programs. Bumpers follow the same network and era matching as commercials, so file them the same way, for example `Bumpers/Adult Swim/2000s`.

If a program's file is missing or fails, its remaining parts and the breaks inside it are skipped together and the break after it starts.

### Speeding through credits

**Credits** on a channel's Content tab can speed through end credits at 2×, 4× or 8×: the credits roll that much faster while their music keeps playing at normal speed, fading out into the next program. Four minutes of credits at 4× take one minute. A scene after the credits plays at normal speed. The credits part is encoded while it plays.

This needs to know where the credits are: an Outro media segment (added by plugins such as Intro Skipper), a chapter named like `Credits`, `End Credits`, `Outro` or `Ending`, or what **Credits detection** found. Credits must start in the second half of the program and last at least ten seconds; opening credits are never sped up. Programs whose credits aren't found play them normally, and Channel health says when no upcoming program has any. Commercial breaks inside a program always fall before its credits.

### Credits detection

**Credits detection** (on by default) finds where the credits start in every program on your channels, so programs need no chapters or other plugins. It starts after Jellyfin has finished starting and the first schedule is built, then reads each file once, in the background, one at a time, at low priority, beginning with whatever airs soonest:

- **Episodes** are compared with the episodes next to them in the same show. Shows close with the same music every week, so the longest stretch of matching sound (at least 15 seconds) in the last quarter of an episode is its credits.
- **Movies**, and shows whose credits change every episode, are checked for a long, mostly black stretch near the end (at least 20 seconds), which is what credits on a black background look like. Only keyframes are decoded, at a tiny size.

Results are saved in `library-tv/credits.json` in Jellyfin's data folder. A file that changes is read again; a program where nothing was found is tried again after two weeks. When a scan finds new credits, the planned guide is rebuilt once, after the current program, so breaks and **Credits** speed use them. Chapters and Outro segments take priority when a program has them. Open **Credits detection** in the builder to turn it off or see its progress.

### Smart channels

Under **Automatic channels → Smart channels**, **+ Add smart channel** builds a channel from what is in your library instead of from a hand-picked list:

- **Libraries to search** (tick one or more), then any combination of:
- **Genres (any of)**: tick several, taken from the genres in your library, plus a box for others.
- **Decades (any of)**: 1920s to 2020s.
- **Specific years**: a list with ranges, such as `1985, 1994-1999`.
- **Shows and movies that always air**: chosen with the usual picker. These air whether or not they match the rest.

A program is included when it has any of the chosen genres **and** comes from any of the chosen years or decades (a criterion you leave empty matches everything). The always-air titles are added on top. A channel with no libraries chosen airs only its chosen titles. Years and decades use each program's air or release year (episodes use their air date), and programs with no known year are left out when years or decades are chosen. Smart channels get a free number if you leave it blank, use the automatic channels' commercials per break and the shared commercial library, keep their episode progress when disabled, and are included in settings backups. For finer control, use the filters on a normal channel's content selection.

### Block timing

Each time block has three timing settings on the Schedule tab:

- **Start: Soft** (the default) waits for the program and break before it. **Start: Exact** lets nothing run past the block's start: the planner only picks programs, with their breaks, that end in time, fills the rest with commercials and bumpers that fit, and leaves any final seconds as a short gap. The block then starts on the minute. Programs and clips never overrun it.
- **Skip the block when it would start more than N minutes late** (soft blocks): if the program before it was already running when the block was due and the block would begin later than this, the block is skipped for that day and the default lineup airs until the block ends. 0 never skips. A block never counts as late because the guide was built partway through it.
- **Fill: Programs per day** is the original behaviour. **Fill: Whole programs that fit** airs only programs (with their breaks) that end before the block does, then fills the leftover time with clips. The default lineup is not used for the leftover time.

A block's **Guide title** replaces the program name in the Live TV guide for every program in it (for example "Saturday Morning Cartoons"); the real title is shown in the description. Clip padding fills at most two hours; beyond that the channel waits as a gap, and gaps longer than 15 minutes stop the stream as before.

### Padding to round times

**Start programs on the clock** (Commercials tab) pads each program, after its final break, with commercials and bumpers that fit until the next 5, 10, 15, 20, 30 or 60 minutes on the local clock. A 22-minute episode on a half-hour grid gets about eight minutes of clips and the next program starts on :00 or :30. Clips never run past the next round time or an exact block start. Whatever is shorter than the shortest clip remains a short gap. Padding needs commercials or bumpers (the channel's own or the shared library); Channel health says when there are none.

### Break placement

On the Commercials tab, **Breaks inside a program fall** either **evenly** (the **Breaks during each episode/movie** numbers, at the nearest chapter mark when there is one) or **every N minutes**, with a most-breaks limit per program, snapping to chapter marks within a quarter of the interval. **Never split programs shorter than** replaces the fixed ten-minute rule (10 by default), and **Keep the last minutes of a program free of breaks** keeps the ending unbroken; breaks also stay out of detected credits as before. **Up to this many clips per break** makes break length vary between the usual count and this number, the same way every time the guide is built. **Show programs in the guide without their commercial breaks** lists each program once and runs it through its breaks, which is useful for clients that show a separate row per break.

### Order, turns and filters

Under each content selection:

- **Order** is *Follow the shuffle setting*, *Chronological* (by air or release date, then season and episode) or *Alphabetical*. Changing it reorders what is left of the current cycle.
- **Start the first cycle at a random place** begins at a random show or episode; the show then carries on from there and every episode still airs once per cycle.
- **Keep two-part episodes together** stops a turn ending in the middle of "Part 1" / "Part 2" (and "(1)" / "(2)", "Part One"…). Parts are recognised from the episode name.
- In the **Choose shows & movies** picker, the **Genre** and **From/To year** boxes narrow what you browse (alongside the search box and library), so you can pick from, say, Comedies of the 1990s.
- **Episodes in a row for particular shows** (`Show name = 3` per line) overrides the pool-wide number for those shows.
- **Filters** act like a saved search: genres and tags to include, genres to never include, words the title must or must not contain (any-of, case-insensitive, matched on the show's or movie's title), never include genres, highest content rating (G, TV-Y7, PG, PG-13, R; unrated programs are kept), earliest and latest year, shortest and longest program. Year and rating use Jellyfin's metadata.

### Channel images and stream choices

On a channel's Content tab:

- **Channel logo** is shown for the channel in Live TV. **Offline card** plays instead of black during schedule gaps longer than about eight seconds, as H.264 video with silent AAC.
- **Watermark** draws a logo (position, opacity, width) over everything on the channel, including commercials and bumpers. Because it is drawn on the picture, every program on that channel is encoded, as with captions or credits speed; Channel health says so. A missing image is reported and ignored.
- **Preferred audio track title** picks a track whose title contains the text (for example "Dolby"), among tracks in the preferred language. Without a default track, the track with the most channels is used.

Image settings are full server paths to PNG, JPG or WebP files, entered by an administrator in the builder. They are used as FFmpeg inputs and shown in Live TV, and are never accepted from non-administrators.

### Shared commercial library

Open **Commercial library** to choose commercials and bumpers once for every channel; a channel uses them whenever its own **Commercials** or **Bumpers** selection is empty. Automatic library channels use them too, with **Commercials per break on automatic channels** (2 by default) under **Automatic channels**. New manual channels start with two commercials per break.

When a channel has no breaks, Channel health says why: no commercials selected, **Clips to play in each break** set to 0, or no clip fitting its programs' networks or shows. Point it at a whole commercial library or folder. Files don't need to be sorted into folders: after Jellyfin finishes starting up (the same deferred startup as the rest of the plugin, never during it), and again at each library refresh, Library TV reads every file and folder name for a year (`1994`, `'94`), a decade (`1990s`, `90s`) and a network, and sorts each clip to the programs that match it. **How your clips were sorted** is a grid of networks against decades with the number of clips in each; cells with fewer than eight clips are highlighted, because breaks there borrow clips from the network's other years and then general clips to avoid repeating.

Networks are recognised from the studios and tags of every program on your channels, the networks you type on channels and blocks, and a built-in list of common networks (Nickelodeon, Nick Jr, Cartoon Network, Adult Swim, Toonami, Boomerang, Disney Channel, Fox Kids, Kids WB, ABC, NBC, CBS, FOX, PBS, MTV, Comedy Central, HBO and others). Names that are also everyday words, such as BET or Lifetime, are only recognised when one of your programs lists them. The most specific name wins, so `Fox Kids promo.mp4` is a Fox Kids clip, not a FOX one. A clip that names a known network is never used as a general commercial, even on channels that have no program from that network.

### Browsing and fixing clips

Under **Browse and fix clips**, every shared and channel clip is listed as it is filed (from the last saved refresh): its kind, network, year and show. Search by name or folder, or filter by network, year, kind, or problem (not filed under a network, no year, turned off, edited). Open a clip to:

- **Change its network, year or show**, or turn it off. Leave a field blank to keep what its name says; type `General` to file it under no network; a show makes it play only with that show. Nothing is renamed on disk.
- **Change its kind**, so a clip chosen as a commercial plays as a bumper, or the reverse.
- **Preview** it in your browser (formats the browser can't play still play on your channels).
- See **where it plays**: the programs on your channels it can air with.
- **Make a rule for its folder**.

Tick several clips (or **Select all** matching the current filter) to change their network, year, show or kind, or turn them off, in one step; **Undo my edits** removes the changes. Edits apply when you save, then the guide's future programs are rebuilt after the current program.

**Rules** change every clip whose folder or file name contains some text, for example everything containing `bump` becomes a bumper (a one-click preset), or `/Adult Swim/2000s/` is filed under Adult Swim. Rules apply in order, later ones winning, and edits to single clips win over rules. Rules and edits only re-file clips from the libraries and folders you chose as commercials or bumpers; they are included in settings backups.

### Network-matched commercials

With **Match commercials to the network the program aired on** (on by default), each break uses commercials filed under the program's network. A commercial belongs to a network when a folder or file name in its path names it, for example:

```text
Commercials/
  Adult Swim/2000s/…
  Nickelodeon/1990s/…
  Cartoon Network/…
  Toonami/…
  General/…        ← used for programs from any other network
```

Names match as whole words, ignoring case and punctuation, so `Adult Swim`, `adult_swim` and `Adult Swim Bumps 2004` all match Adult Swim. A name of six or more letters also matches when run together, such as `AdultSwim`. Short names like `ABC` must appear as a separate word. A few nicknames count too: `Nick` is Nickelodeon (but `Nick Jr` stays Nick Jr), `CN` is Cartoon Network, and `Williams Street` is Adult Swim.

The network for a break is the first of these that has commercials:

1. The time block's **Commercial network**, when the program came from that block.
2. The channel's **Commercial network**, for example `Toonami` for a Toonami channel.
3. A tag on the program or its show. Tag a show `Toonami` in Jellyfin to give it Toonami commercials even though its studio is Cartoon Network.
4. The program's studios. For shows, Jellyfin's metadata usually lists the original network here (Nickelodeon, Adult Swim, Cartoon Network); for movies it lists production companies, which rarely match.

A studio, tag or setting also counts when it contains a network's name or nickname, so a show whose studio is "Nickelodeon Animation Studio" or "Cartoon Network Studios" gets that network's clips, and "Williams Street" gets Adult Swim's. If none matches, the break uses only commercials that are not in any network folder, so a kids show never gets another network's adult commercials. If there are no such general clips, the program's breaks are left out rather than filled with another network's clips, and the program is not split for them. Era matching then picks the decade within the chosen commercials. Each network and decade keeps its own rotation.

**Variety.** A break draws from at least eight different clips (or three per commercial slot, if more) when your library has them, so the same few never repeat break after break. The most accurate clips come first: clips made for the show, then the program's network in its decade, the network in any year, general clips from the decade, then any general clips. Less accurate steps are added only while there are too few clips, and never another network's or show's clips. Within the chosen clips every one plays before any repeats. Guide preview's **Why** says when a break was widened, and Channel health warns when a channel still has fewer than eight different commercials.

**Show-specific clips.** A commercial or bumper whose name contains a show or movie title, such as `SpongeBob SquarePants bumper.mp4` or `Simpsons promo 1992.mp4`, plays only with that show, before any network matching. A leading "The" is ignored, and titles shorter than five letters (such as "Up") are never matched, so ordinary words in clip names do not tie them to a show.

**Checking the matches.** **Guide preview → Load guide** labels each clip as a commercial or bumper and shows why it was chosen, for example `Why: Nickelodeon (studio “Nickelodeon Animation Studio”) · 1990s`, `Why: made for SpongeBob SquarePants · 2000s` or `Why: general: no clips for this program's network`. A program whose year is unknown shows `any year`; fix its air date or year in Jellyfin to match its era.

### Era-matched commercials

With **Match commercials to when the program was released or aired** (on by default), each break uses commercials from the decade of the program before it. Episodes use their original air date, falling back to the show's first year; movies use their release year. If you have no commercials from that decade, the closest decade you do have is used. Each decade keeps its own rotation, so clips still take turns.

A commercial's year comes from its folder or file name first, because commercial collections are usually organized that way: `1980s`, `80s`, `1985` and `Cola (1985).mp4` all work, and the last year or decade in the path wins. Jellyfin's production year or premiere date is used only when the path has none. Clips with no year at all are used only after programs whose year is unknown. Turn the option off to draw every break from all selected clips.

### Content restrictions

Open **Content restrictions** to keep matching content off the air during set hours on every channel. For example, block the `Horror` genre from 06:00 until 21:00 so scary movies only air at night. A restriction matches a program when any of its genres or tags matches, ignoring case; episodes use their show's genres and tags. A program is never scheduled if any part of it would be on the air inside the window, so a two-hour horror movie cannot start at 20:00 under a window ending at 21:00. Windows can span midnight and belong to the day they start.

Restricted programs are skipped for now and stay at the front of the rotation, so they air at the first allowed time. If nothing left in the current cycle is allowed, the least recently aired allowed item plays again rather than leaving dead air; shows still continue from their next episode. A time block with nothing allowed falls back to the default lineup. If the default lineup has nothing allowed either, the channel has a gap until the next allowed program, and Channel health warns about it. Viewers stay connected through gaps of up to 15 minutes and can tune in during one.

### Channel numbers and automatic stations

Edit **Channel number** at the top of a manual channel. Use 1–999999, optionally followed by a dot or hyphen and a 0–9999 subchannel. For example, `7`, `7.2`, and `7-10` are separate channels, ordered by major number then subchannel. `007.02` and `7-2` refer to the same number for conflict checking. Enabled channels need unique numbers across manual and automatic stations. Renumbering keeps the channel ID, existing guide and episode progress.

Under **Automatic channels**, select a library and edit its **Custom channel name** and **Custom channel number**. Leave the name blank to follow the library name; leave the number blank to assign the next free whole number on save. **Starting number** affects new assignments only. Existing assignments remain fixed; reserved assignments for deselected libraries are retained to make reselecting predictable. The global automatic-channel switch can temporarily hide the automatic lineup while keeping selected libraries' schedule progress. Deselecting a library removes its saved schedule; its number/name reservation remains.

New manual channels use the first free positive whole number instead of adding one to the highest subchannel. Duplicate-number errors identify both stations. Disabled manual channels may share a number with an enabled station, but must be given a free number before re-enabling. Their numbers remain reserved for automatic allocation.

By default time blocks have **soft boundaries**: a program and its commercial break finish before the next block is evaluated, so a long preceding movie can delay or entirely cover a short block (see [Block timing](#block-timing) for exact starts, allowed lateness and fitted blocks). When blocks overlap, the first matching block in the list wins. A block spanning midnight belongs to the day it started. Daily quotas apply to that local start date; daylight-saving changes follow the selected time zone.

Advanced source settings (also retained in the configuration API):

| Setting | Meaning |
| --- | --- |
| `LibraryIds` | Recursively include selected Jellyfin libraries |
| `ItemIds` | Include particular videos or recursively include series/folders |
| `FolderIds` | Recursively include particular indexed folders |
| `Genres`, `Tags` | Optional case-insensitive filters; match any value within each filter |
| `Shuffle` | Shuffle series/movies between cycles; preserve order inside each series |
| `IncludeSpecials` | Include TV season zero; false by default |
| `RepeatWhenExhausted` | Repeat only after the pool is exhausted; true by default |
| `EpisodesInARow` | Episodes of one series aired before the next series takes a turn, 0–100; 2 by default. 0 airs each series straight through |

Library, folder and item selectors are combined. An empty source selects nothing. Genre and tag filters also match episodes whose genres or tags are set on their show. Individual item IDs preserve insertion order when shuffle is disabled; series episodes are always sorted by season/episode. With multiple series, each series airs `EpisodesInARow` episodes per turn, and each series's turns are spread evenly through the cycle, so a short show is placed among a long show's turns instead of all at the start. Movies take one turn each. Every selected item still airs once before the pool repeats. Existing channels switch to this order at their next refresh without losing their place; programs already in the guide keep their order until they air, or use **Rebuild future slots** to apply it now.

## Updating schedules

Saving settings refreshes the plugin schedule. When a channel's content, time blocks, commercial breaks, commercials, bumpers, credits speed or the content restrictions change, its future guide is rebuilt automatically after the current program, the same way as **Guide preview → Rebuild future slots**; renaming, renumbering and playback settings (picture shape, captions, audio language) never rebuild it. **Rebuild future slots** remains for applying newly added media sooner. The current program and its commercial break finish, then reversible future entries are rebuilt from the saved settings. Episode progress, movie rotation and daily quotas are rewound only for discarded future selections. Newly generated entries carry compact undo metadata; no complete rotation snapshot is stored for every program.

Schedules created before v0.4 do not contain enough information to safely rewind every selection. Their existing future entries stay protected until they air; any newer tail can be rebuilt. The builder reports the actual **Applied from** time. This preserves episode progress instead of guessing at an older cursor. A failed scan or write keeps the previous published schedule and saved state.

Each channel's new guide generation is capped at 10,000 program/ad entries. Very short clips can hit that cap before filling the requested horizon; Channel health explains the limit. Existing older guide entries are not silently truncated on upgrade.

The plugin refresh and Jellyfin's guide import are separate. Jellyfin imports updated programs through its standard guide refresh task. Schedule that task more frequently if needed. Automatic channel creation is per selected library; automatic genre-based channels and a visual drag-and-drop schedule editor are not implemented.

Schedule and rotation state are stored in `<Jellyfin data directory>/library-tv/schedule.json`. Include this file and the plugin XML configuration in backups. Don't delete state to fix a playback issue: doing so resets progress. Downtime of up to seven days beyond the last planned entry is advanced in bounded catch-up; longer downtime resumes at the saved rotation rather than simulating every missed day.

### Playback recovery and diagnostics

If FFmpeg sends nothing for 12 seconds (20 seconds while starting), for example because a network mount hangs, or exits partway through a program, the plugin restarts that program at the current position. Viewers stay connected, and the connection is kept busy with MPEG-TS filler packets while it restarts. A program is restarted at most twice before it is treated as unplayable. If a file ends more than five seconds before its listed runtime, the next program starts at once instead of leaving dead air.

If a watched program is missing, unreadable or fails to produce a transport stream, the plugin brings the next scheduled item forward and shifts the later guide times by the same amount. Rotation selections remain consumed once; stale recovery requests cannot skip twice. Recovery is limited to three consecutive failed items per viewing session (two advances, then stop), with no background retry loop. Successful playback resets the consecutive-failure count. If FFmpeg itself cannot start, playback stops with a configuration message and the schedule is not advanced. Because Jellyfin keeps its own copy of the guide, a recovery queues Jellyfin's **Refresh Guide** task once so the shifted times appear; it is skipped if that task is already running or queued.

Channel health shows source/schedule warnings, active viewers, recovery count, playback mode, how the current program is being played (copied or encoded) and the most recent playback issue. It does not probe media or run FFmpeg. Playback diagnostics are kept in bounded memory, not a growing history file. A refresh records its elapsed time; this is a diagnostic measurement, not a server-wide CPU benchmark.

FFmpeg is killed and awaited on cancellation, output failure or a stall. No media process runs when nobody is watching. Recovery does not guarantee seamless playback on every client or reconnect clients that independently disconnect; a client whose own player stalls may need to be restarted. Real FFmpeg tests validate compatible program/commercial transitions and cleanup; real client testing is still needed.

If a pool is empty or exhausted with repeats disabled, its time block falls back to the default source. If both are unavailable, the guide has a gap rather than silently repeating episodes. Recordings are not supported by this provider.

### Settings backups

Open **Backup & restore settings → Download settings backup** to export saved channel definitions and general scheduling settings. Unsaved edits, private stream tokens, server connection addresses, guide files and episode progress are excluded. Select a JSON backup to review its channel names/numbers, then choose **Restore reviewed settings** and confirm replacement. The import limit is 1 MB and 100 manual channels; imported settings receive the same validation as normal saves. The current server's connection address and token are preserved, and matching channel IDs retain their schedule state. Library/item GUIDs are server-specific, so this is not an automatic media-ID migration to another server.

For a full disaster-recovery backup, also preserve the plugin XML configuration and `library-tv/schedule.json` privately. The downloadable settings backup intentionally contains no playback history or episode cursor.

## Server networking

`InternalBaseUrl` is the Jellyfin URL reachable **from its own process**, normally `http://127.0.0.1:8096`. Include a configured Jellyfin base path, for example `http://127.0.0.1:8096/jellyfin`. Docker uses the container's port, not the host's mapped port. Loopback HTTPS requires a trusted certificate. The transport endpoint only accepts loopback connections with a private random token; users tune through Jellyfin's normal authorized Live TV endpoints. Administrative configuration and library discovery require the administrator policy.

The private transport token is stored in the plugin configuration and can appear in server diagnostics. Treat configuration and logs as private. Stream sources are exclusively drawn from local indexed library items; there is no shell command construction or arbitrary path API.

## Build and release

Requires .NET 10 SDK; the official Jellyfin 12.2 Controller package targets .NET 10.

```powershell
dotnet build src/LibraryTV/LibraryTV.csproj -c Release
dotnet run --project tests/Planner.Tests -c Release
dotnet run --project tests/Integration.Tests -c Release
./scripts/package.ps1 -Version 0.10.0.0 -Changelog "Describe this release."
```

The visual-builder interaction tests require Node.js 22+:

```text
npm ci --ignore-scripts
npm test
```

Real transport tests use `LIBRARY_TV_FFMPEG` pointing to a local FFmpeg executable, with `ffprobe` in the same directory. CI installs its own test tools on the GitHub runner and sets this variable. The test tools are never bundled into the plugin or installed onto the user's server.

CI builds and tests on Linux. Pushing an annotated four-part version tag such as `git tag -a v0.10.0.0 -m "What changed"` tests, packages and publishes release assets, then updates the catalog on `main`. The tag message becomes the catalog changelog and release notes; a tag without a message is rejected. Without a local clone, open **Actions → Release → Run workflow** on GitHub and enter the version and release notes: the run tests the `main` branch, creates the tag, and publishes the same way. The workflow requires permission to write repository contents and push to `main`. Catalog checksums use Jellyfin's required MD5; releases also contain SHA256SUMS.

Tests cover block timing (exact starts, lateness, fitted blocks), clock padding, interval breaks, orders, two-part episodes, per-show turns, watermarks and offline cards with real FFmpeg, episode continuation across lineups and blocks, content restrictions, network- and era-matched commercials, breaks inside programs, bumpers, picture shapes, burned-in captions and their timing, sped-up credits, automatic rebuilds after settings changes, copy/encode decisions, stall restarts, rotation exhaustion, additions/deletions, guide stability, commercials, local/overnight blocks, actual Jellyfin API compatibility, embedded dashboard resources, XML configuration round-tripping and atomic schedule storage. They do not establish real-client playback compatibility or measured CPU usage.

License: GPL-3.0-only.
