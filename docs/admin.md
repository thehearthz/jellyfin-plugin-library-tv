# Administration and development

Navigation integration, backups, building and releasing.

[← Back to the README](../README.md)

## Plugin Pages integration

To reach the builder from Jellyfin's main web navigation:

1. Add the upstream repository `https://www.iamparadox.dev/jellyfin/plugins/manifest.json` under **Dashboard → Plugins → Repositories**.
2. Install **File Transformation** and **Plugin Pages 3.0+**, using builds compatible with Jellyfin 12.2, then restart Jellyfin.
3. Refresh the web client. Open **Library TV Builder** in the **Plugin Settings** menu section provided by Plugin Pages.

Library TV registers the page automatically through the [documented Plugin Pages v3 interface](https://github.com/IAmParadox27/jellyfin-plugin-pages#adding-your-pages). No manual JSON or web-file edits are needed. Its connection status appears under **Server settings & Plugin Pages** in the builder. The menu entry can be visible to all signed-in users, but only administrators can browse media or edit channels; other users receive an administrator-access message.

Plugin Pages and File Transformation are optional navigation dependencies and are not bundled in Library TV. The same full builder always remains available at **Dashboard → Plugins → Library TV**. The integration is for Jellyfin Web and compatible web-wrapper clients; native clients may not expose Plugin Pages navigation. The integration is tested against upstream's reflection contract, not a running installation of the companion plugins.

Jellyfin's plugin catalog and repository configuration are documented [here](https://jellyfin.org/docs/general/server/plugins/). Release ZIPs contain only the plugin DLL; Jellyfin supplies its own dependencies. Manual installation: extract the DLL into a `Library TV` directory under your Jellyfin plugins directory and restart.

## Settings backups

Open **Backup & restore settings → Download settings backup** to export saved channel definitions and general scheduling settings. Unsaved edits, private stream tokens, server connection addresses, guide files and episode progress are excluded. Select a JSON backup to review its channel names/numbers, then choose **Restore reviewed settings** and confirm replacement. The import limit is 1 MB and 100 manual channels; imported settings receive the same validation as normal saves. The current server's connection address and token are preserved, and matching channel IDs retain their schedule state. Library/item GUIDs are server-specific, so this is not an automatic media-ID migration to another server.

For a full disaster-recovery backup, also preserve the plugin XML configuration and `library-tv/schedule.json` privately. The downloadable settings backup intentionally contains no playback history or episode cursor.

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
