# AutoPlaylist

A Jellyfin plugin that builds curated music playlists from your own library using a
local LLM served by [Ollama](https://ollama.com).

The model is shown what your library actually contains and chooses the tracks; the
plugin handles everything else — reading the library, writing the playlists, and
running on a schedule. It is not a smart-playlist generator: there are no genre rules
or automatic selection filters.

It is designed to run unattended overnight: leave the scheduled task enabled and each
morning the library has a new playlist in it, built on a fresh idea, plus a refreshed
selection of whatever you added recently. Curating one playlist takes a while — the
model is asked about a thousand candidate tracks in batches — which is exactly why it
belongs at 3am rather than in front of you.

## What it comes up with

The model is asked for an idea the library can actually support — not a genre bucket.
In practice it reaches for moods and times of day, activities, scenes and eras with a
point of view, the world around one anchor artist, mixes bound by feel rather than
genre, and timely editorial hooks such as an anniversary or a film. Year buckets
("Class of 2008", "Class of 2009") are explicitly discouraged.

A real example, produced by a 35B model against an 18,000-track library:

> **The Alchemist's Desk** — pop and rock records where studio production, tape
> manipulation and sonic layering serve as the primary compositional force.

That is the difference from a rule-based smart playlist: "records where production is
the composition" is not something you can express as a filter over genre tags and
years, but a model can recognise it track by track. The description is written into the
playlist's overview in Jellyfin, so each playlist explains its own premise.

## Requirements

- Jellyfin 10.11 or newer
- An Ollama server reachable from Jellyfin, with an instruct model available

## Install

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:

   ```
   https://raw.githubusercontent.com/yonie/jellyfin-plugin-autoplaylist/main/manifest.json
   ```

2. Install **AutoPlaylist** from **Catalog**, then restart Jellyfin.
3. Open **Dashboard → Plugins → AutoPlaylist**, set the Ollama URL, click
   **Test connection & load models**, select a model, and save.
4. Click **Curate now**, or wait for the scheduled run.

To install manually, download the zip from
[Releases](https://github.com/yonie/jellyfin-plugin-autoplaylist/releases) and extract
it into `<jellyfin data>/plugins/AutoPlaylist/`.

## How it works

Each run:

1. Reads the library and builds a summary of the artists, genres, decades and albums
   it contains, with track counts.
2. Asks the model for one playlist idea the library can support, along with the artists
   it wants to draw from. Ideas already used are excluded.
3. Presents real candidate tracks in batches — title, album, year, length, genre — and
   the model selects the ones that fit.
4. Applies the quality rules in code: per-artist and per-album limits, one version per
   song (studio preferred), track length bounds, and exclusion of holiday music unless
   the playlist is about it. If the result falls below the configured minimum number of
   tracks or artists, the idea is discarded rather than padded.
5. Writes the playlist in shuffled order and tags it.

If enabled, one playlist is also rebuilt on every run from the most recently added
albums.

## Playlist ownership

Every playlist the plugin creates is tagged `jl-autoplaylist`. Only playlists carrying
that tag are ever rebuilt or deleted — playlists you created yourself are never
modified. The tag lives in Jellyfin, so no local state file is needed.

## Privacy

- The plugin contacts one external service: the Ollama URL you configure. There is no
  telemetry.
- Sent to the model: artist names, track titles, album names, years, track lengths and
  genre tags, plus the names of playlists the plugin created.
- Not sent: your own playlists, playback history, user accounts, or server details.
- Pointing the plugin at a hosted model instead of a local one means those prompts
  leave your machine.

## Configuration

| Setting | Default | Description |
|---|---|---|
| Ollama server URL | `http://localhost:11434` | The only external dependency |
| Model | *(required)* | Any instruct model |
| New playlists per run | 1 | Playlists created per run |
| Target tracks | 200 | Target length; never padded to reach it |
| Max per artist / album | 8 / 5 | Diversity limits |
| Minimum tracks / artists | 50 / 15 | Below this, the idea is discarded |
| Track length bounds | 100–420s | Excludes interludes and long jams |
| Ownership tag | `jl-autoplaylist` | Defines what the plugin may modify |
| Extra curator instructions | *(empty)* | Appended to the built-in prompt |

Remaining settings — temperature, context size, timeouts, candidate batch sizes and
retry counts — are documented on the settings page.

## Scheduled runs

The plugin installs a task at **Dashboard → Scheduled Tasks → AutoPlaylist → Curate
playlists**, set to 03:15 daily. Each run adds *New playlists per run* playlists and
rebuilds the recently-added one, so the collection grows by one idea a night without
you doing anything.

Raise *New playlists per run* to build a starting collection faster, and set *Stop
after this many playlists* if you want it to fill up to a fixed size and then leave the
library alone. A run can also be started by hand with **Curate now** on the settings
page, which streams the same activity log the scheduled run writes.

## Building

The plugin targets .NET 9. CI builds on every push; pushing a `v*` tag publishes a
release and updates `manifest.json`.

```bash
dotnet publish Jellyfin.Plugin.AutoPlaylist/Jellyfin.Plugin.AutoPlaylist.csproj -c Release -o publish
python3 .github/scripts/package.py --version 1.0.0.0 --tag v1.0.0.0 --repo yonie/jellyfin-plugin-autoplaylist
```

## License

MIT — see [LICENSE](LICENSE).
