using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AutoPlaylist.Configuration;

namespace Jellyfin.Plugin.AutoPlaylist.Curation;

/// <summary>
/// The curator's brief. This is the method the project was built around: the model
/// picks every track by musical judgment, and code never picks for it.
/// </summary>
public static class CurationPrompts
{
    /// <summary>
    /// Schema for the angle proposal reply.
    /// </summary>
    public const string AngleSchema = """
    {
      "type": "object",
      "properties": {
        "name": { "type": "string" },
        "description": { "type": "string" },
        "seed_artists": { "type": "array", "items": { "type": "string" } },
        "search_terms": { "type": "array", "items": { "type": "string" } },
        "reasoning": { "type": "string" }
      },
      "required": ["name", "description", "seed_artists"]
    }
    """;

    /// <summary>
    /// Schema for a track selection reply.
    /// </summary>
    public const string PicksSchema = """
    {
      "type": "object",
      "properties": {
        "picks": { "type": "array", "items": { "type": "integer" } }
      },
      "required": ["picks"]
    }
    """;

    /// <summary>
    /// Schema for a written playlist description.
    /// </summary>
    public const string DescriptionSchema = """
    {
      "type": "object",
      "properties": {
        "description": { "type": "string" }
      },
      "required": ["description"]
    }
    """;

    /// <summary>
    /// The description used for the continuously refreshed recently-added playlist.
    /// </summary>
    public const string FreshFindsDescription =
        "A curated cross-section of the music most recently added to the library — " +
        "not a mood, the binding idea is recency.";

    /// <summary>
    /// Builds the system prompt shared by every call in a run.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The system prompt.</returns>
    public static string SystemPrompt(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var prompt = new StringBuilder(3000);
        prompt.Append(
            """
            You are a music curator working with someone's personal Jellyfin music library.
            You choose every track by musical judgment, the way a good human playlist-maker
            would. Genre tags, years and search terms exist only to show you what the library
            contains — they never make the decision for you. Genre tags in particular are
            unreliable and often wrong about a record; judge the music, not the label.

            What makes a playlist proper:
            - Coherent. Every track plausibly belongs to the theme. For a bangers theme, skip
              the ballads; for a mellow theme, skip the bangers.
            - Diverse. Many artists. One artist or album must never dominate the list.
            - Studio versions. Skip live, remix, karaoke, instrumental and "(Deluxe)"
              duplicates of a song you already have.
            - Songs, not fragments or epics. The backbone should be tracks of roughly two to
              six minutes; skits, intros and interludes break the flow, and very long jams
              dominate it.
            - No Christmas or holiday music unless the playlist is explicitly about it. Holiday
              tracks hide in album and track titles even when the genre says something else.
            - Fresh. Never re-skin an angle that already exists — find a genuinely different
              slice of the library.
            - If the library cannot support an idea well, drop the idea and choose a
              better-stocked one. A thin, padded playlist is worse than a different, good one.

            Answer with JSON only. No prose, no explanation outside the JSON.
            """);

        if (!string.IsNullOrWhiteSpace(config.ExtraInstructions))
        {
            prompt.AppendLine().AppendLine().AppendLine("Additional instructions from the library owner:")
                .Append(config.ExtraInstructions.Trim());
        }

        return prompt.ToString();
    }

    /// <summary>
    /// Asks for one new playlist angle this library can actually sustain.
    /// </summary>
    /// <param name="index">The library index.</param>
    /// <param name="existingNames">Names of playlists this plugin already made.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="rejected">Angles rejected earlier in this run, with the reason.</param>
    /// <returns>The user prompt.</returns>
    public static string AnglePrompt(
        LibraryIndex index,
        IReadOnlyCollection<string> existingNames,
        PluginConfiguration config,
        IReadOnlyCollection<string>? rejected = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(existingNames);
        ArgumentNullException.ThrowIfNull(config);

        var prompt = new StringBuilder(8000);
        prompt.AppendLine(index.BuildSnapshot(config.MaxArtistsInPrompt, 30));

        if (existingNames.Count > 0)
        {
            prompt.AppendLine("Playlists you have already made here — do not repeat or re-skin these angles:");
            prompt.AppendJoin(" · ", existingNames);
            prompt.AppendLine().AppendLine();
        }

        if (rejected is { Count: > 0 })
        {
            prompt.AppendLine("Angles already tried in this run and rejected:");
            foreach (var reason in rejected)
            {
                prompt.Append("- ").AppendLine(reason);
            }

            prompt.AppendLine();
        }

        prompt.Append(CultureInfo.InvariantCulture, $"""
        Propose ONE new playlist for this library, aiming for about {config.TargetTrackCount} tracks.

        Reach for angles with character, and invent your own rather than copying these shapes:
        moods and times of day; activities (focus, workout, dinner); scenes and eras with a
        point of view; the world around one artist; cross-genre vibes that ignore genre lines;
        an editorial hook such as an anniversary or a producer. Avoid lazy time-slicing — a wall
        of "Class of 2008", "Class of 2009" is not curation.

        Judge honestly whether the artists listed above can carry the idea for
        {config.TargetTrackCount} tracks across at least {config.MinArtistCount} different artists.
        If not, choose a better-stocked angle.

        Reply with JSON:
          name           a short evocative playlist title (max 40 characters)
          description    one sentence describing the playlist, for its Jellyfin overview
          seed_artists   20 to 45 artist names, copied exactly from the list above, whose
                         catalogues you want to look through for this playlist
          search_terms   0 to 6 words to scan titles, albums and genres for extra candidates
          reasoning      one sentence on why this library can carry it
        """);

        return prompt.ToString();
    }

    /// <summary>
    /// Asks the model to write the description for a playlist that already exists, by
    /// reading a sample of what is actually on it.
    /// </summary>
    /// <param name="playlistName">The playlist name.</param>
    /// <param name="sample">A sample of the playlist's tracks, one per line.</param>
    /// <returns>The user prompt.</returns>
    public static string DescriptionPrompt(string playlistName, string sample)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            """
            PLAYLIST: {0}

            A sample of what is on it:

            {1}

            Write the description that belongs in this playlist's Jellyfin overview: one
            sentence, at most 180 characters, telling a listener what this playlist is —
            its mood, era, scene or thread. Be concrete rather than generic. Do not name
            any artist, album or track, and do not describe it as a "collection of songs".

            Reply with JSON: the description under the key "description".
            """,
            playlistName,
            sample);
    }

    /// <summary>
    /// Asks the model to pick the tracks that belong, from one batch of real candidates.
    /// </summary>
    /// <param name="name">The playlist name.</param>
    /// <param name="description">The playlist description.</param>
    /// <param name="batch">The candidate batch.</param>
    /// <param name="batchNumber">The one-based batch number.</param>
    /// <param name="batchCount">The total number of batches.</param>
    /// <param name="chosenSoFar">How many tracks have been chosen so far.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The user prompt.</returns>
    public static string SelectionPrompt(
        string name,
        string description,
        IReadOnlyList<TrackCandidate> batch,
        int batchNumber,
        int batchCount,
        int chosenSoFar,
        PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(config);

        var prompt = new StringBuilder(8000);
        prompt.Append(CultureInfo.InvariantCulture, $"""
        PLAYLIST: {name}
        {description}

        You are working through the library in batches. This is batch {batchNumber} of {batchCount}.
        You have chosen {chosenSoFar} tracks so far, aiming for about {config.TargetTrackCount}.
        At most {config.MaxTracksPerArtist} tracks by any one artist and {config.MaxTracksPerAlbum} from
        any one album across the whole playlist, so leave room for artists in later batches.

        Pick only the tracks that genuinely belong on this playlist. Skipping a whole batch is a
        valid answer — most batches should yield only a handful of tracks. Do not pick a track
        just because its artist fits; the track itself has to fit.

        CANDIDATES:

        """);

        foreach (var (track, i) in batch.Select((t, i) => (t, i)))
        {
            prompt.AppendLine(track.ToPromptLine(i + 1));
        }

        prompt.Append(CultureInfo.InvariantCulture, $$"""

        Reply with JSON: {"picks": [numbers of the tracks you include]}
        Use only numbers between 1 and {{batch.Count}}. An empty list is fine.
        """);

        return prompt.ToString();
    }
}
