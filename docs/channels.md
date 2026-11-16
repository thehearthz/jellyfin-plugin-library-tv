# Building channels

How to build channels: content, schedules, timing, automatic and smart channels.

[← Back to the README](../README.md)

## Build an evening channel

1. Click **New channel**, enter a name and number, then open **Content → Choose shows & movies**.
2. In **Movies**, choose movies for the default lineup. Use the search box and library filter to narrow the list. Click **Done**. These movies fill the time outside your scheduled blocks.
3. Open **Schedule → Add time block**. Choose 18:00–23:00, select the days, and set **Programs per day** to 3.
4. In that block's content picker, select a show. Disable shuffle if you want series in the order selected. Episode order inside each show is preserved with either setting. Select several shows and set **Episodes in a row** (2 by default) to rotate between them.
5. Open **Commercials**, set two clips per break, and select an indexed commercial folder. Add the folder to a Jellyfin video library first; the picker does not scan arbitrary filesystem paths. Name subfolders by network and decade, such as `Nickelodeon/1990s` and `Adult Swim/2000s`, so each program gets commercials from its own network and era.
6. Click **Save channels**. The **Guide preview** tab shows the saved schedule; it does not simulate unsaved edits.

This schedules three episodes during the evening block, then returns to movies. Episode rotation is independent for each channel and each block: day one 1/2/3, day two 4/5/6, and so on. The default source must provide enough repeatable content to keep the rest of the day full. Selections can include specific shows, movies, indexed folders, or entire libraries. Shows automatically include their available episodes.

## Collections

Jellyfin collections (box sets) only link to their movies and shows, which live in other libraries, so a library made of collections has no media of its own. Library TV opens each collection and airs what it links to, including shows inside a collection and collections inside collections. This applies to an automatic channel for your Collections library, to a smart channel's libraries, and to any content selection. The **Collections** tab in the **Choose shows & movies** picker selects individual collections, so a channel can air just *Star Wars* and *Marvel*, and a smart channel can always air a collection.

## Smart channels

Under **Automatic channels → Smart channels**, **+ Add smart channel** builds a channel from what is in your library instead of from a hand-picked list:

- **Libraries to search** (tick one or more), then any combination of:
- **Genres (any of)**: tick several, taken from the genres in your library, plus a box for others.
- **Decades (any of)**: 1920s to 2020s.
- **Specific years**: a list with ranges, such as `1985, 1994-1999`.
- **Shows and movies that always air**: chosen with the usual picker. These air whether or not they match the rest.

A program is included when it has any of the chosen genres **and** comes from any of the chosen years or decades (a criterion you leave empty matches everything). The always-air titles are added on top. A channel with no libraries chosen airs only its chosen titles. Years and decades use each program's air or release year (episodes use their air date), and programs with no known year are left out when years or decades are chosen. Smart channels get a free number if you leave it blank, use the automatic channels' commercials per break and the shared commercial library, keep their episode progress when disabled, and are included in settings backups. For finer control, use the filters on a normal channel's content selection.

## Block timing

Each time block has three timing settings on the Schedule tab:

- **Start: Soft** (the default) waits for the program and break before it. **Start: Exact** lets nothing run past the block's start: the planner only picks programs, with their breaks, that end in time, fills the rest with commercials and bumpers that fit, and leaves any final seconds as a short gap. The block then starts on the minute. Programs and clips never overrun it.
- **Skip the block when it would start more than N minutes late** (soft blocks): if the program before it was already running when the block was due and the block would begin later than this, the block is skipped for that day and the default lineup airs until the block ends. 0 never skips. A block never counts as late because the guide was built partway through it.
- **Fill: Programs per day** is the original behaviour. **Fill: Whole programs that fit** airs only programs (with their breaks) that end before the block does, then fills the leftover time with clips. The default lineup is not used for the leftover time.

A block's **Guide title** replaces the program name in the Live TV guide for every program in it (for example "Saturday Morning Cartoons"); the real title is shown in the description. Clip padding fills at most two hours; beyond that the channel waits as a gap, and gaps longer than 15 minutes stop the stream as before.

## Seasonal blocks

A time block can run only between two dates each year: fill in **Only from** and **Until** as month-day (`12-01` to `12-25`). A range like `12-20` to `01-05` runs over New Year. Outside its dates the block doesn't exist and the rest of the schedule airs as normal.

- Put seasonal blocks **first** in the list: when two blocks cover the same time, the first one wins.
- A block from `00:00` to `00:00` runs all day, so a seasonal block can take over a whole channel for the season.
- Use the block's own content and filters to choose what airs, for example tag your holiday episodes `Christmas` and set **Tags** to `Christmas`, or use **Title contains** with words like `Christmas, Halloween`.

Example: on a sitcom channel, add a block `00:00`–`00:00`, dates `12-01` to `12-25`, with **Tags: Christmas**. Every December the channel shows holiday episodes, then returns to its usual lineup on the 26th.

## Padding to round times

**Start programs on the clock** (Commercials tab) pads each program, after its final break, with commercials and bumpers that fit until the next 5, 10, 15, 20, 30 or 60 minutes on the local clock. A 22-minute episode on a half-hour grid gets about eight minutes of clips and the next program starts on :00 or :30. Clips never run past the next round time or an exact block start. Whatever is shorter than the shortest clip remains a short gap. Padding needs commercials or bumpers (the channel's own or the shared library); Channel health says when there are none.

## Order, turns and filters

Under each content selection:

- **Order** is *Follow the shuffle setting*, *Chronological* (by air or release date, then season and episode) or *Alphabetical*. Changing it reorders what is left of the current cycle.
- **Start the first cycle at a random place** begins at a random show or episode; the show then carries on from there and every episode still airs once per cycle.
- **Keep two-part episodes together** stops a turn ending in the middle of "Part 1" / "Part 2" (and "(1)" / "(2)", "Part One"…). Parts are recognised from the episode name.
- In the **Choose shows & movies** picker, the **Genre** and **From/To year** boxes narrow what you browse (alongside the search box and library), so you can pick from, say, Comedies of the 1990s.
- **Episodes in a row for particular shows** (`Show name = 3` per line) overrides the pool-wide number for those shows.
- **Skip what this person has watched** (under Filters) leaves out episodes and movies a chosen Jellyfin user has played. It's checked at each refresh. Once everything has been watched, everything airs again, so the channel never goes blank. Shows chosen to always air are not affected, and watching on Live TV doesn't mark anything as played.
- **Filters** act like a saved search: genres and tags to include, genres to never include, words the title must or must not contain (any-of, case-insensitive, matched on the show's or movie's title), never include genres, highest content rating (G, TV-Y7, PG, PG-13, R; unrated programs are kept), earliest and latest year, shortest and longest program. Year and rating use Jellyfin's metadata.

## Content restrictions

Open **Content restrictions** to keep matching content off the air during set hours on every channel. For example, block the `Horror` genre from 06:00 until 21:00 so scary movies only air at night. A restriction matches a program when any of its genres or tags matches, ignoring case; episodes use their show's genres and tags. A program is never scheduled if any part of it would be on the air inside the window, so a two-hour horror movie cannot start at 20:00 under a window ending at 21:00. Windows can span midnight and belong to the day they start.

Restricted programs are skipped for now and stay at the front of the rotation, so they air at the first allowed time. If nothing left in the current cycle is allowed, the least recently aired allowed item plays again rather than leaving dead air; shows still continue from their next episode. A time block with nothing allowed falls back to the default lineup. If the default lineup has nothing allowed either, the channel has a gap until the next allowed program, and Channel health warns about it. Viewers stay connected through gaps of up to 15 minutes and can tune in during one.

## Channel numbers and automatic stations

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
