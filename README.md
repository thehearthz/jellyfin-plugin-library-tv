# Library TV for Jellyfin 12.2

Build always-on television channels from local media already indexed by Jellyfin. **Experimental v0.1:** the plugin compiles against the official 12.2 API, with automated scheduling and integration checks. Playback transitions have not yet been verified on a real Jellyfin server/client combination. Please test with a small channel first.

## Install

1. In Jellyfin **Dashboard → Plugins → Repositories**, add a repository named **Library TV** with this URL:

   ```text
   https://raw.githubusercontent.com/thehearthz/jellyfin-plugin-library-tv/main/manifest.json
   ```

2. Open the plugin catalog, install **Library TV**, then restart Jellyfin.
3. Open **Dashboard → Plugins → Library TV**. Set your schedule time zone, such as `America/New_York`.
4. Enable automatic channels and select libraries, or add a manual channel template and fill in its source IDs. Save and refresh.
5. Run Jellyfin's **Refresh Internet Channels** scheduled task (the translated task name may differ). A separate **Library TV** folder appears under **My Media** on the home screen, alongside your movie and show libraries. Open it and play a channel to join its current program. You do not need to create a filesystem library or add `.strm` files.
6. Run Jellyfin's **Refresh Guide** task to also show channels and schedules under **Live TV**. Give viewers normal Jellyfin Live TV access.

Upgrading from v0.1.0.0: install the v0.1.1.0 update, restart Jellyfin, then run **Refresh Internet Channels**. If your home screen is customized, enable **Library TV** in the user's home-screen settings and refresh the client. Some clients group plugin channel folders differently; the exact placement depends on the client. The standard Jellyfin home views include this provider as its own folder. The folder's items keep stable names; the current program schedule is available in Live TV's Guide.

Jellyfin's plugin catalog and repository configuration are documented [here](https://jellyfin.org/docs/general/server/plugins/). Release ZIPs contain only the plugin DLL; Jellyfin supplies its own dependencies. Manual installation: extract the DLL into a `Library TV` directory under your Jellyfin plugins directory and restart.

## What it does

- A separate **Library TV** home folder, with one playable live item per enabled channel, alongside the existing Live TV guide integration.
- Manual channels and automatically generated channels for explicitly selected libraries.
- Rolling 1–7 day schedule, extended periodically from current library metadata.
- Saved rotation state survives restarts. Episodes within each series follow season/episode order, even with shuffle enabled.
- A pool airs all its selected content before cycling. New items enter the next unplanned portion of the rotation. Disable `RepeatWhenExhausted` to stop a pool when it runs out.
- Daily/weekly time blocks, local time zones, overnight blocks, and per-block daily episode limits.
- Commercial breaks from indexed library folders, with configurable number of clips between programs. Selected commercials are excluded from that channel's main content pools.
- One FFmpeg process per actively watched channel shared by viewers. No FFmpeg processes when channels are idle. Bounded viewer queues and a simultaneous channel limit keep resource use controlled.
- Viewers join the program scheduled for the current time. The schedule continues while nobody watches.

## Low CPU and playback constraints

Default playback uses FFmpeg **stream copy**: it remuxes local video/audio into MPEG-TS without encoding. Choose consistent video and audio codecs, resolution, audio layout and frame rate across programs and ads. H.264 video with AAC audio is a useful starting point. Files must be locally readable by the Jellyfin process and contain a video stream. Remote streams, DRM, images, music-only channels, subtitles and live inputs are not supported.

Stream copy seeking is keyframe-limited. Joins may begin slightly before the exact schedule position. At file transitions the transport signals a discontinuity; tolerance varies between Jellyfin clients. Arbitrary mixed-codec libraries are **not guaranteed** to play seamlessly.

Set a channel's `Transcode` to `true` to normalize its output to H.264/AAC at 720p/30fps using software encoding. This costs materially more CPU and requires Jellyfin's FFmpeg to include libx264. It is a compatibility option, not a promise of seamless transitions. Jellyfin may additionally transcode Live TV for a particular client. Hardware acceleration for the plugin's encoding process is not implemented.

## Manual channel example

The dashboard shows library IDs. Copy series, episode, or folder IDs from Jellyfin item URLs. `ItemIds` accepts videos or series/folder IDs; `FolderIds` recursively selects videos in an indexed folder. Nothing scans an arbitrary filesystem directory: add the commercial folder to a Jellyfin video library first.

```json
[
  {
    "Id": "evening-tv",
    "Name": "Evening TV",
    "Number": "1",
    "Enabled": true,
    "Source": {
      "LibraryIds": ["YOUR_MOVIES_LIBRARY_GUID"],
      "Shuffle": true,
      "RepeatWhenExhausted": true
    },
    "Blocks": [
      {
        "Id": "daily-show",
        "Start": "18:00",
        "End": "23:00",
        "Days": [0, 1, 2, 3, 4, 5, 6],
        "MaxItemsPerDay": 3,
        "Source": {
          "ItemIds": ["YOUR_SERIES_GUID"],
          "Shuffle": false,
          "IncludeSpecials": false,
          "RepeatWhenExhausted": true
        }
      }
    ],
    "Commercials": {
      "FolderIds": ["YOUR_COMMERCIAL_FOLDER_GUID"],
      "Shuffle": true,
      "RepeatWhenExhausted": true
    },
    "CommercialCount": 2,
    "Transcode": false
  }
]
```

This example schedules three episodes during the evening block, then returns to movies. Episode rotation is independent for each channel and each block: day one 1/2/3, day two 4/5/6, and so on. The default source must provide enough repeatable content to keep the rest of the day full.

Time blocks have **soft boundaries**: a program and its commercial break finish before the next block is evaluated. A long preceding movie can delay or entirely cover a short block. A strict start-time/cutoff system is not implemented. When blocks overlap, the first matching block in the list wins. A block spanning midnight belongs to the day it started. Daily quotas apply to that local start date; daylight-saving changes follow the selected time zone.

`Source` fields:

| Setting | Meaning |
| --- | --- |
| `LibraryIds` | Recursively include selected Jellyfin libraries |
| `ItemIds` | Include particular videos or recursively include series/folders |
| `FolderIds` | Recursively include particular indexed folders |
| `Genres`, `Tags` | Optional case-insensitive filters; match any value within each filter |
| `Shuffle` | Shuffle series/movies between cycles; preserve order inside each series |
| `IncludeSpecials` | Include TV season zero; false by default |
| `RepeatWhenExhausted` | Repeat only after the pool is exhausted; true by default |

Library, folder and item selectors are combined. An empty source selects nothing. Individual item IDs preserve insertion order when shuffle is disabled; series episodes are always sorted by season/episode. With multiple series, one series runs through its available episodes before the next; interleaved episode batching is not implemented.

## Updating schedules

Saving settings refreshes the plugin schedule. New guide entries extend the unplanned tail; existing planned entries remain fixed so that refreshing cannot consume episodes twice or change an in-progress broadcast. New media can therefore take up to the configured guide horizon to appear. For a completely new lineup, use a new channel ID, save, then run Jellyfin's **Refresh Guide** task.

The plugin refresh and Jellyfin's guide import are separate. Jellyfin imports updated programs through its standard guide refresh task. Schedule that task more frequently if needed. Automatic channel creation is per selected library; automatic genre-based channels and a visual drag-and-drop schedule editor are not implemented.

Schedule and rotation state are stored in `<Jellyfin data directory>/library-tv/schedule.json`. Include this file and the plugin XML configuration in backups. Don't delete state to fix a playback issue: doing so resets progress. Downtime of up to seven days beyond the last planned entry is advanced in bounded catch-up; longer downtime resumes at the saved rotation rather than simulating every missed day.

Missing/unsupported files stop a watched stream; automatic failover/reconnection is not implemented. Refresh removes deleted items from future rotation, while already planned entries remain fixed. If a pool is empty or exhausted with repeats disabled, its time block falls back to the default source. If both are unavailable, the guide has a gap rather than silently repeating episodes. Recordings are not supported by this provider.

## Server networking

`InternalBaseUrl` is the Jellyfin URL reachable **from its own process**, normally `http://127.0.0.1:8096`. Include a configured Jellyfin base path, for example `http://127.0.0.1:8096/jellyfin`. Docker uses the container's port, not the host's mapped port. Loopback HTTPS requires a trusted certificate. The transport endpoint only accepts loopback connections with a private random token; users tune through Jellyfin's normal authorized Live TV endpoints. Administrative configuration and library discovery require the administrator policy.

The private transport token is stored in the plugin configuration and can appear in server diagnostics. Treat configuration and logs as private. Stream sources are exclusively drawn from local indexed library items; there is no shell command construction or arbitrary path API.

## Build and release

Requires .NET 10 SDK; the official Jellyfin 12.2 Controller package targets .NET 10.

```powershell
dotnet build src/LibraryTV/LibraryTV.csproj -c Release
dotnet run --project tests/Planner.Tests -c Release
dotnet run --project tests/Integration.Tests -c Release
./scripts/package.ps1 -Version 0.1.0.0
```

CI builds and tests on Linux. Pushing a four-part version tag such as `v0.1.1.0` tests, packages and publishes release assets, then updates the catalog on `main`. The workflow requires permission to write repository contents and push to `main`. Catalog checksums use Jellyfin's required MD5; releases also contain SHA256SUMS.

Tests cover episode continuation, rotation exhaustion, additions/deletions, guide stability, commercials, local/overnight blocks, actual Jellyfin API compatibility, embedded dashboard resources, XML configuration round-tripping and atomic schedule storage. They do not establish real-client playback compatibility or measured CPU usage.

License: GPL-3.0-only.
