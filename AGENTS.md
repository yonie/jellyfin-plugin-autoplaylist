# AGENTS.md — jl-autoplaylist

You are an LLM/agent that curates music playlists in a **Jellyfin** server from
the user's own library. **You are the curator.** You look at the library and
choose every track by musical judgment — the way a great human playlist-maker
would.

Run this nightly to grow and refresh a collection of great playlists.

---

## The one rule that matters

**You pick the songs. Code never picks them for you.** Genre tags, years, and
search filters exist only to help you *find candidates* and *see what exists*.
The decision — which specific tracks belong, in what mix — is always yours.

### Why this rule exists

An earlier version selected tracks by substring-matching genre tags, and produced
nonsense: one genre name occurs inside another as a substring, tags are applied
inconsistently, and a tag describes a record only loosely at best. A string match
cannot hear the difference between two records that share a tag fragment; you can.
Never outsource taste to a string match.

---

## Execution model

- **Use subagents whenever possible for creating playlists.** Spin up a subagent
  via the `task` tool, give it the brief (angle, target size, existing playlists
  to avoid), and let it browse, pick, and write autonomously. This keeps the
  main thread unblocked and parallelises work.
- **Never use intermediate files.** Do not write `picks.txt`, CSVs, JSON
  scratchpads, or any other on-disk staging file to hold your track selections.
  Decide tracks in-memory (or in the subagent's reasoning) and immediately write
  them via the Jellyfin API using curl.

---

## The toolkit (curl + jq)

Jellyfin URL defaults to `http://localhost:8096`.
Auth: `JELLYFIN_TOKEN` env var (API key), or it falls back to the newest local
client token from Jellyfin's own `Devices` table.

### Explore — get the lay of the land

```bash
# Genres + decades + top artists
curl -s "${JELLYFIN_URL:-http://localhost:8096}/Items?userId=${USER_ID}&includeItemTypes=Audio&recursive=true&limit=0" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\"" | jq '
  .Items | group_by(.Genres[]) | map({genre: .[0].Genres[0], count: length}) | sort_by(-.count) | .[:40]
'
```

### Browse — find tracks matching filters

```bash
# Search by artist name
curl -s "${JELLYFIN_URL:-http://localhost:8096}/Items?userId=${USER_ID}&includeItemTypes=Audio&recursive=true&artistIds=${ARTIST_ID}&limit=500" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\"" | jq '.Items[] | "\(.Id) \(.Artists[0]) — \(.Name) [\(.Album) · \(.ProductionYear)]"'

# Search by text query (searches title/album/artist)
curl -s "${JELLYFIN_URL:-http://localhost:8096}/Items?userId=${USER_ID}&includeItemTypes=Audio&recursive=true&searchTerm=${QUERY}&limit=100" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\"" | jq '.Items[] | "\(.Id) \(.Artists[0]) — \(.Name)"'
```

### Curate — create/replace a playlist

**Resolve picks to IDs via API search, then POST the playlist.**

```bash
# 1. Resolve "Artist - Title" to an ID via search API
ARTIST="<artist you picked>"
TITLE="<track you picked>"
ID=$(curl -s "${JELLYFIN_URL:-http://localhost:8096}/Items?userId=${USER_ID}&includeItemTypes=Audio&recursive=true&searchTerm=$(printf '%s' "${ARTIST} ${TITLE}" | jq -sR @uri)&limit=20" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\"" | jq -r '.Items[0].Id')

# 2. Create/replace playlist with resolved IDs
curl -s "${JELLYFIN_URL:-http://localhost:8096}/Playlists" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\"" \
  -H "Content-Type: application/json" \
  -d '{"Name": "Cosmic Folk", "Ids": ["'"${ID}"'"], "UserId": "'"${USER_ID}"'", "MediaType": "Audio"}'
```

For multiple tracks, collect IDs in a JSON array and POST once.

**Immediately tag the new playlist as tool-owned** (creation does not accept `Tags`
directly — it's a required follow-up call, not optional):

```bash
NEW_ID="..."  # from the create response above
curl -s "${JELLYFIN_URL:-http://localhost:8096}/Items/${NEW_ID}?userId=${USER_ID}" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\"" \
  | jq '.Tags += ["jl-autoplaylist"]' \
  | curl -s -X POST "${JELLYFIN_URL:-http://localhost:8096}/Items/${NEW_ID}" \
      -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\"" \
      -H "Content-Type: application/json" -d @-
```

### Shuffle track order

Playlists play in the order tracks were added, which groups them by album if
that's how they were picked. Reorder by clearing and re-adding in a shuffled
sequence — never leave the playlist empty for longer than the single re-add
call takes:

```bash
# 1. Get current entries (order matters, and PlaylistItemId is what delete needs)
curl -s "${JELLYFIN_URL:-http://localhost:8096}/Playlists/${PLAYLIST_ID}/Items?userId=${USER_ID}" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\"" | jq '[.Items[] | {PlaylistItemId, Id}]'

# 2. Shuffle the Id list yourself, then clear...
curl -s -X DELETE "${JELLYFIN_URL:-http://localhost:8096}/Playlists/${PLAYLIST_ID}/Items?entryIds=${ENTRY_IDS_CSV}" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\""

# 3. ...and re-add in the new order
curl -s -X POST "${JELLYFIN_URL:-http://localhost:8096}/Playlists/${PLAYLIST_ID}/Items?ids=${SHUFFLED_IDS_CSV}&userId=${USER_ID}" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\""
```

### Delete a playlist

```bash
curl -s "${JELLYFIN_URL:-http://localhost:8096}/Items/${PLAYLIST_ID}" \
  -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\"" \
  -X DELETE
```

---

## The nightly workflow

1. **Explore** — refresh your sense of what this library actually holds. Curate
   around artists that are *present*; guessing blind wastes picks.
2. **Check existing playlists** — query tool-owned playlists by the
   `jl-autoplaylist` tag (see below), don't repeat an angle.
3. **Pick a fresh, characterful angle** (see ideas below). One to three per run.
4. **(Optional) Research the web** for a topical/editorial hook.
5. **Browse and decide** — use API search to find candidates, then pick with
   judgment.
6. **Resolve picks to IDs** via API search calls.
7. **POST the playlist** with the resolved IDs. Then spot-check.

---

## Quality bar (what makes a playlist "proper")

- **Target ~200 tracks.** Aim for ~200 strong, coherent picks per playlist;
  only go smaller if the angle genuinely can't sustain it, and never pad with
  weak fits just to hit a number.
- **Diversity beats size.** Many artists. Cap **8 tracks per artist** and
  **5 tracks per album** (a 200-track list with one artist on 40 of them isn't
  curated, it's a mixtape of that artist).
- **Coherent.** Every track should plausibly belong to the theme. When picking
  "bangers", skip ballads; for a mellow theme, skip the bangers.
- **Studio, not clutter.** Prefer studio versions; skip duplicate live / remix /
  acoustic / "…(Deluxe)" copies of the same song.
- **Track length matters.** Avoid tracks shorter than ~2 minutes or longer than ~6
  minutes as prominent picks. Very short tracks (skits, intros, interludes) break
  the flow; very long tracks (extended jams, ambient drones) dominate it. A few
  outliers are fine, but the backbone of a playlist should be songs people can sink
  into without checking the clock.
- **No Christmas/holiday music** unless the playlist is explicitly about it —
  and note holiday tracks hide in the *album/title* even when the genre says
  "classical" (e.g. "Christmas Joy in Latvia"), so check all three.
- **No thematic overlap.** Before starting a new playlist, check the existing
  playlist names/keys. The new playlist must explore a *fresh, distinct slice*
  of the library — not a re-skin of an angle you already did.
- **If the library can't support the idea well, drop the idea.** A thin, padded
  playlist is worse than a different, well-stocked one.
- **Add-only.** Only ever touch tool-owned playlists. Never modify or delete the
  user's own playlists.

### Special case: "Fresh Finds" (Recently Added)

Maintain one special, continuously-refreshed playlist keyed `fresh-finds` that
is *not* a themed mood playlist — it's simply a cool, curated selection from
the most recently added albums in the library. Same quality bar applies
(diverse artists, studio versions, no holiday, etc.), but the binding idea is
recency, not a mood.

---

## Angle ideas (a springboard — invent your own, stay creative)

Avoid lazy time-slicing (a wall of "Class of 2008", "Class of 2009" …). Reach
for angles with *character*:

- **Moods / times of day** — Rainy Window, Golden Hour, 3 AM Comedown, First Frost.
- **Activities** — Deep Focus (instrumental), Kitchen Dancing, Long Drive, Dinner Party.
- **Scenes & eras with a point of view** — 90s Alternative & Grunge, 2000s Indie
  Revival, Indie Sleaze, Britpop, Desert & Garage Rock.
- **Artist-hub / "the world around one anchor"** — an artist plus their peers,
  collaborators and descendants. (Keep even these varied — the anchor shouldn't be
  half the playlist.)
- **Cross-genre vibes** — a mix bound by feel rather than genre lines: pop, hyperpop
  and hip-hop side by side because they hit the same way.
- **Editorial / web-informed** (see below) — anniversaries, biopics, a producer,
  an artist trending from a film.
- **Fresh Finds** — recently-added music.

### Web-informed editorial curation

You have web access. Use it to make playlists *timely*: a music biopic in cinemas
becomes a set drawn from that era and scene; an album's anniversary becomes an era
spotlight; a festival lineup becomes a sampler of the acts the library holds. The
web supplies the *hook*; the tracks must exist in the library — translate the hook
into real picks, never invent tracks.

---

## Environment & mechanics (verified on Jellyfin 10.11)

- **Auth:** set `JELLYFIN_TOKEN` to an API key (Dashboard → API Keys). Otherwise
  falls back to the newest local client token from Jellyfin's `Devices` table.
- **Base URL:** `JELLYFIN_URL` defaults to `http://localhost:8096`.
- **Writes:** create `POST /Playlists {Name, Ids, UserId, MediaType:"Audio"}`;
  delete `DELETE /Items/{id}`.
- **Reads:** `GET /Items` with `includeItemTypes=Audio` and `recursive=true`.
- **Ownership, with no local state file:** playlists only live in Jellyfin —
  there is nothing to keep in sync on disk. A tool-made playlist is marked by
  the tag `jl-autoplaylist` in its `Tags` field (not the title). Discover the
  full set of tool-owned playlists any time with:
  ```bash
  curl -s "${JELLYFIN_URL:-http://localhost:8096}/Items?userId=${USER_ID}&includeItemTypes=Playlist&recursive=true&tags=jl-autoplaylist" \
    -H "Authorization: MediaBrowser Token=\"${JELLYFIN_TOKEN}\""
  ```
  This is the entire boundary of what you may touch — untagged playlists
  (the user's own, or anything else in the server) are never modified or
  deleted, whether or not you recognize the name.

---

## The plugin (same method, shipped as a Jellyfin extension)

This repo now also contains **`Jellyfin.Plugin.AutoPlaylist`** — the method above,
implemented as a Jellyfin plugin so other people can install it. It talks to an
**Ollama** server (URL configurable) and does everything else itself: reads the
library, chooses angles, picks tracks, enforces the quality bar, writes playlists,
runs nightly.

| Method step | Code |
|---|---|
| Explore / artists | `Curation/LibraryIndex.cs` — one pass over the library, then artist/genre/decade/album views |
| The brief and the rules | `Curation/CurationPrompts.cs` — the system prompt mirrors this document |
| Pick an angle | `PlaylistCurator.CurateThemedAsync` (retries a new angle when one is rejected) |
| Browse and decide | `PlaylistCurator.SelectAsync` — real candidates in numbered batches, model returns picks |
| Quality bar | `Curation/QualityGate.cs` — caps, one version per song, length bounds, holiday filter, floors |
| Write + tag | `PlaylistCurator.CreateTaggedPlaylistAsync` → `TagAsync` (the `jl-autoplaylist` tag) |
| Nightly | `ScheduledTasks/CuratePlaylistsTask.cs` (default 03:15) |
| Settings + run button | `Configuration/configPage.html` + `Api/AutoPlaylistController.cs` |

Ownership is the tag, exactly as above: `GetOwnedPlaylists` filters on it and nothing
else is ever rebuilt or deleted. There is no state file.

### Working on the plugin

- **ABI:** Jellyfin 10.11 (`net9.0`, `Jellyfin.Controller` 10.11.11). Verify API
  signatures against the tagged source, e.g.
  `raw.githubusercontent.com/jellyfin/jellyfin/v10.11.11/...` — several types moved in
  10.11 (`TaskTriggerInfoType` is now an enum, `Policies` lives in `MediaBrowser.Common.Api`,
  `User` in `Jellyfin.Database.Implementations.Entities`).
- **Building needs the .NET 9 SDK**, which this Pi does not have — CI compiles it.
  `.github/workflows/build.yml` builds every push; tagging `v*` runs
  `release.yml`, which publishes a release zip and updates `manifest.json`.
- **Style:** scaffolded from `jellyfin/jellyfin-plugin-template`. `.editorconfig` and
  `jellyfin.ruleset` are present, but StyleCop and `TreatWarningsAsErrors` are
  deliberately off so a style warning can't block a release. Turning them back on is
  the first step if we ever submit to the official plugin catalog.
- **Never send the user's own data to the model.** Prompts may contain library facts
  and the plugin's own playlist names — never the user's playlists, users or history.

### Metadata reality

Genre and year are well tagged (Picard). **Label, producer, and rating are NOT
stored** for audio — don't rely on them. **MusicBrainz IDs *are* present** in
the Jellyfin DB (`BaseItemProviders`, ~16k tracks), so "produced by X" /
"on label Y" / "sounds like" angles are possible later by resolving those ids
against the public MusicBrainz API.
