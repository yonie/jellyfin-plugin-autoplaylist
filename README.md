# AutoPlaylist — jl-autoplaylist

A **Jellyfin plugin** that turns your music library into *your own Spotify*: a growing
set of thematic, genuinely curated playlists.

The playlists are chosen by an LLM you host yourself, served by
[Ollama](https://ollama.com). The plugin shows the model what your library actually
holds, and the model picks every track by musical judgment. It is deliberately **not** a
smart-playlist generator — there are no genre rules or auto-selection filters.

Everything else — reading the library, curating, writing playlists, running nightly —
is handled by the plugin. The only thing to configure is your Ollama URL.

## Why an LLM instead of genre rules

Because genre rules produce nonsense. An early filter-based version of this project put
Paul McCartney's *Ram* into a teen-pop playlist — its genre tag `Classic Uk Pop`
contains the string `k pop`. A filter can't tell *Ram* from a K-pop single; a model can.
So the model picks, and code only enforces the mechanical rules it can't see across
batches.

## Requirements

- Jellyfin **10.11** or newer
- An Ollama server reachable from your Jellyfin server, with one instruct model pulled

## Install

1. In Jellyfin: **Dashboard → Plugins → Repositories → +**, and add:

   ```
   https://raw.githubusercontent.com/yonie/jellyfin-plugin-autoplaylist/main/manifest.json
   ```

2. **Catalog → AutoPlaylist → Install**, then restart Jellyfin.
3. **Dashboard → Plugins → AutoPlaylist**: set your Ollama URL, click
   *Test connection & load models*, pick a model, and **Save**.
4. Click **Curate now** to build the first playlist while you watch the log, or wait for
   the nightly task.

Prefer to install by hand? Grab the zip from
[Releases](https://github.com/yonie/jellyfin-plugin-autoplaylist/releases) and unpack it into
`<jellyfin data>/plugins/AutoPlaylist/`.

## How it works

Each run:

1. **Reads the library** — every artist, genre, decade and album that exists, with real
   track counts.
2. **Chooses an angle** — the model proposes one playlist it believes the library can
   carry, avoiding angles it already made, and names the artists it wants to look
   through. Moods, activities, scenes, artist-hubs, editorial hooks — not year buckets.
3. **Picks the tracks** — real candidate tracks are shown in batches (title, album,
   year, length, genre) and the model chooses the ones that belong. Most batches yield
   only a handful; skipping is a valid answer.
4. **Enforces the quality bar in code** — the rules a model can't police across batches:
   per-artist and per-album caps, one version per song (studio preferred), track-length
   bounds, no holiday music outside holiday playlists, and a floor of tracks and
   distinct artists. **If the result is below the floor, the angle is dropped instead of
   padded.**
5. **Writes the playlist**, shuffled, tagged as its own.

There's also one continuously refreshed playlist built from the most recently added
albums — the same quality bar, but the binding idea is recency rather than a mood.

## Your playlists are never touched

The plugin marks every playlist it creates with the tag **`jl-autoplaylist`**. That tag
is the entire boundary of what it will ever rebuild or delete. Playlists without it —
including every playlist you made yourself — are never modified, reordered or deleted,
whether or not the name looks familiar. Ownership lives in Jellyfin, so there is no
state file to drift out of sync.

## Privacy

- The plugin talks to exactly one external service: the Ollama URL you configure.
  There is no telemetry and no other outbound call.
- What gets sent: artist names, track titles, album names, years, lengths and genre
  tags from your library, plus the names of playlists this plugin created.
- What never gets sent: your own playlists, your watch history, your users, or anything
  identifying your server.
- If you point it at a hosted model rather than a local one, those prompts leave your
  machine — same as any cloud LLM.

## Configuration

Everything lives in the plugin's settings page. The defaults are sane; the ones worth
knowing:

| Setting | Default | Meaning |
|---|---|---|
| Ollama server URL | `http://localhost:11434` | The only external dependency |
| Model | *(pick one)* | Any instruct model; bigger curates better |
| New playlists per run | 1 | Playlists added per nightly run |
| Target tracks | 200 | Aim per playlist, never padded to reach it |
| Max per artist / album | 8 / 5 | Diversity caps enforced in code |
| Minimum tracks / artists | 50 / 15 | The floor below which an angle is dropped |
| Track length bounds | 100–420s | Keeps out skits, intros and 12-minute jams |
| Ownership tag | `jl-autoplaylist` | The boundary of what the plugin may touch |
| Extra curator instructions | *(empty)* | Appended to the built-in brief — your taste |

The nightly run is **Dashboard → Scheduled Tasks → AutoPlaylist → Curate playlists**
(default 03:15). Change or disable the schedule there.

## Building from source

The plugin is a .NET 9 class library; CI builds and packages it on every push, and
tagging `v*` publishes a release plus an updated `manifest.json`.

```bash
dotnet publish Jellyfin.Plugin.AutoPlaylist/Jellyfin.Plugin.AutoPlaylist.csproj -c Release -o publish
python3 .github/scripts/package.py --version 1.0.0.0 --tag v1.0.0.0 --repo yonie/jellyfin-plugin-autoplaylist
```

## The method

[`AGENTS.md`](AGENTS.md) is the curation method this plugin implements, written for a
human or an agent to follow directly against Jellyfin's REST API. The plugin's built-in
prompt mirrors it.

## License

MIT — see [`LICENSE`](LICENSE).
