# Commercials, bumpers and credits

How breaks are placed and how clips are matched to programs.

[← Back to the README](../README.md)

## Commercial breaks and bumpers

**Clips to play in each break** sets how many commercials each break has. A break plays after every program, and **Breaks during each episode** (1 by default) and **Breaks during each movie** (2 by default) add breaks inside programs, 0–5 each. A program is split evenly, or at the chapter mark nearest each split when the file has chapters (DVD and Blu-ray rips often mark act breaks). Programs shorter than ten minutes are never split, and nothing is split while **Clips to play in each break** is 0. Each part continues exactly where the last one stopped; the Live TV guide still lists the program once. When video is copied rather than encoded, a later part starts on the keyframe just before the cut and repeats those few seconds, because copied video can only start on a keyframe; the program's final seconds are shortened by the same amount.

Select clips under the **Bumpers** tab to add bumpers. One plays going into each break and one coming out of it; on a channel without commercials, one bumper plays between programs. Bumpers follow the same network and era matching as commercials, so file them the same way, for example `Bumpers/Adult Swim/2000s`.

If a program's file is missing or fails, its remaining parts and the breaks inside it are skipped together and the break after it starts.

## Break placement

On the Commercials tab, **Breaks inside a program fall** either **evenly** (the **Breaks during each episode/movie** numbers, at the nearest chapter mark when there is one) or **every N minutes**, with a most-breaks limit per program, snapping to chapter marks within a quarter of the interval. **Never split programs shorter than** replaces the fixed ten-minute rule (10 by default), and **Keep the last minutes of a program free of breaks** keeps the ending unbroken; breaks also stay out of detected credits as before. **Up to this many clips per break** makes break length vary between the usual count and this number, the same way every time the guide is built. **Show programs in the guide without their commercial breaks** lists each program once and runs it through its breaks, which is useful for clients that show a separate row per break.

## Shared commercial library

Open **Commercial library** to choose commercials and bumpers once for every channel; a channel uses them whenever its own **Commercials** or **Bumpers** selection is empty. Automatic library channels use them too, with **Commercials per break on automatic channels** (2 by default) under **Automatic channels**. New manual channels start with two commercials per break.

When a channel has no breaks, Channel health says why: no commercials selected, **Clips to play in each break** set to 0, or no clip fitting its programs' networks or shows. Point it at a whole commercial library or folder. Files don't need to be sorted into folders: after Jellyfin finishes starting up (the same deferred startup as the rest of the plugin, never during it), and again at each library refresh, Library TV reads every file and folder name for a year (`1994`, `'94`), a decade (`1990s`, `90s`) and a network, and sorts each clip to the programs that match it. **How your clips were sorted** is a grid of networks against decades with the number of clips in each; cells with fewer than eight clips are highlighted, because breaks there borrow clips from the network's other years and then general clips to avoid repeating.

Networks are recognised from the studios and tags of every program on your channels, the networks you type on channels and blocks, and a built-in list of common networks (Nickelodeon, Nick Jr, Cartoon Network, Adult Swim, Toonami, Boomerang, Disney Channel, Fox Kids, Kids WB, ABC, NBC, CBS, FOX, PBS, MTV, Comedy Central, HBO and others). Names that are also everyday words, such as BET or Lifetime, are only recognised when one of your programs lists them. The most specific name wins, so `Fox Kids promo.mp4` is a Fox Kids clip, not a FOX one. A clip that names a known network is never used as a general commercial, even on channels that have no program from that network.

## Browsing and fixing clips

Under **Browse and fix clips**, every shared and channel clip is listed as it is filed (from the last saved refresh): its kind, network, year and show. Search by name or folder, or filter by network, year, kind, or problem (not filed under a network, no year, turned off, edited). Open a clip to:

- **Change its network, year or show**, or turn it off. Leave a field blank to keep what its name says; type `General` to file it under no network; a show makes it play only with that show. Nothing is renamed on disk.
- **Change its kind**, so a clip chosen as a commercial plays as a bumper, or the reverse.
- **Preview** it in your browser (formats the browser can't play still play on your channels).
- See **where it plays**: the programs on your channels it can air with.
- **Make a rule for its folder**.

Tick several clips (or **Select all** matching the current filter) to change their network, year, show or kind, or turn them off, in one step; **Undo my edits** removes the changes. Edits apply when you save, then the guide's future programs are rebuilt after the current program.

**Rules** change every clip whose folder or file name contains some text, for example everything containing `bump` becomes a bumper (a one-click preset), or `/Adult Swim/2000s/` is filed under Adult Swim. Rules apply in order, later ones winning, and edits to single clips win over rules. Rules and edits only re-file clips from the libraries and folders you chose as commercials or bumpers; they are included in settings backups.

## Network-matched commercials

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

## Era-matched commercials

With **Match commercials to when the program was released or aired** (on by default), each break uses commercials from the decade of the program before it. Episodes use their original air date, falling back to the show's first year; movies use their release year. If you have no commercials from that decade, the closest decade you do have is used. Each decade keeps its own rotation, so clips still take turns.

A commercial's year comes from its folder or file name first, because commercial collections are usually organized that way: `1980s`, `80s`, `1985` and `Cola (1985).mp4` all work, and the last year or decade in the path wins. Jellyfin's production year or premiere date is used only when the path has none. Clips with no year at all are used only after programs whose year is unknown. Turn the option off to draw every break from all selected clips.

## Speeding through credits

**Credits** on a channel's Content tab can speed through end credits at 2×, 4× or 8×: the credits roll that much faster while their music keeps playing at normal speed, fading out into the next program. Four minutes of credits at 4× take one minute. A scene after the credits plays at normal speed. The credits part is encoded while it plays.

This needs to know where the credits are: an Outro media segment (added by plugins such as Intro Skipper), a chapter named like `Credits`, `End Credits`, `Outro` or `Ending`, or what **Credits detection** found. Credits must start in the second half of the program and last at least ten seconds; opening credits are never sped up. Programs whose credits aren't found play them normally, and Channel health says when no upcoming program has any. Commercial breaks inside a program always fall before its credits.

## Credits detection

**Credits detection** (on by default) finds where the credits start in every program on your channels, so programs need no chapters or other plugins. It starts after Jellyfin has finished starting and the first schedule is built, then reads each file once, in the background, one at a time, at low priority, beginning with whatever airs soonest:

- **Episodes** are compared with the episodes next to them in the same show. Shows close with the same music every week, so the longest stretch of matching sound (at least 15 seconds) in the last quarter of an episode is its credits.
- **Movies**, and shows whose credits change every episode, are checked for a long, mostly black stretch near the end (at least 20 seconds), which is what credits on a black background look like. Only keyframes are decoded, at a tiny size.

Results are saved in `library-tv/credits.json` in Jellyfin's data folder. A file that changes is read again; a program where nothing was found is tried again after two weeks. When a scan finds new credits, the planned guide is rebuilt once, after the current program, so breaks and **Credits** speed use them. Chapters and Outro segments take priority when a program has them. Open **Credits detection** in the builder to turn it off or see its progress.
