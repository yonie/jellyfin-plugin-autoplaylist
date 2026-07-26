using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AutoPlaylist.Configuration;

namespace Jellyfin.Plugin.AutoPlaylist.Curation;

/// <summary>
/// The deterministic half of the method. The model decides what belongs; this enforces
/// the mechanical rules it cannot see across batches — duplicates, holiday music,
/// runaway artists and albums, and the floor below which a playlist is not proper.
/// </summary>
public static class QualityGate
{
    private static readonly string[] _holidayMarkers =
    [
        "christmas", "xmas", "x-mas", "holiday", "noel", "noël", "santa", "jingle",
        "sleigh", "silent night", "advent", "weihnacht", "navidad", "yule"
    ];

    private static readonly string[] _variantMarkers =
    [
        "live", "remix", "karaoke", "instrumental", "acoustic", "demo", "radio edit",
        "reprise", "medley", "session", "rehearsal", "a cappella", "acapella",
        "backing track", "cover version"
    ];

    /// <summary>
    /// Applies every mechanical rule and returns the playlist as it should be written.
    /// </summary>
    /// <param name="picks">The model's picks, in pick order.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="holidayThemed">True when the angle is explicitly about holiday music.</param>
    /// <param name="random">The shuffle source.</param>
    /// <returns>The gate result.</returns>
    public static GateResult Apply(
        IEnumerable<TrackCandidate> picks,
        PluginConfiguration config,
        bool holidayThemed,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(picks);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(random);

        var result = new GateResult();
        var kept = new List<TrackCandidate>();
        var seenIds = new HashSet<Guid>();

        foreach (var track in picks)
        {
            if (!seenIds.Add(track.Id))
            {
                continue;
            }

            if (config.ExcludeHolidayMusic && !holidayThemed && IsHoliday(track))
            {
                result.DroppedHoliday++;
                continue;
            }

            if (track.Seconds > 0
                && (track.Seconds < config.MinTrackSeconds || track.Seconds > config.MaxTrackSeconds))
            {
                result.DroppedLength++;
                continue;
            }

            kept.Add(track);
        }

        if (config.PreferStudioVersions)
        {
            var best = new Dictionary<string, TrackCandidate>(StringComparer.Ordinal);
            foreach (var track in kept)
            {
                var key = SongKey(track);
                if (!best.TryGetValue(key, out var incumbent))
                {
                    best[key] = track;
                    continue;
                }

                result.DroppedDuplicate++;
                if (Preference(track) < Preference(incumbent))
                {
                    best[key] = track;
                }
            }

            kept = best.Values.ToList();
        }

        // Shuffle before capping so the survivors are spread across artists and albums
        // rather than front-loaded by whichever batch answered first. Playlists play in
        // insertion order, so this is also the final running order.
        for (var i = kept.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (kept[i], kept[j]) = (kept[j], kept[i]);
        }

        var perArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perAlbum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var final = new List<TrackCandidate>(Math.Min(kept.Count, config.TargetTrackCount));

        foreach (var track in kept)
        {
            if (final.Count >= config.TargetTrackCount)
            {
                break;
            }

            perArtist.TryGetValue(track.Artist, out var artistCount);
            if (config.MaxTracksPerArtist > 0 && artistCount >= config.MaxTracksPerArtist)
            {
                result.DroppedArtistCap++;
                continue;
            }

            perAlbum.TryGetValue(track.AlbumKey, out var albumCount);
            if (config.MaxTracksPerAlbum > 0 && albumCount >= config.MaxTracksPerAlbum)
            {
                result.DroppedAlbumCap++;
                continue;
            }

            perArtist[track.Artist] = artistCount + 1;
            perAlbum[track.AlbumKey] = albumCount + 1;
            final.Add(track);
        }

        result.Tracks = final;
        result.DistinctArtists = final.Select(t => t.Artist).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return result;
    }

    /// <summary>
    /// Detects holiday music in genre, album and title — it hides in all three.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <returns>True when the track looks like holiday music.</returns>
    public static bool IsHoliday(TrackCandidate track)
    {
        ArgumentNullException.ThrowIfNull(track);

        foreach (var marker in _holidayMarkers)
        {
            if (track.Title.Contains(marker, StringComparison.OrdinalIgnoreCase)
                || track.Album.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var genre in track.Genres)
            {
                if (genre.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Detects whether an angle is itself about holiday music, in which case the
    /// holiday filter must not apply.
    /// </summary>
    /// <param name="text">The playlist name and description.</param>
    /// <returns>True when the angle is holiday themed.</returns>
    public static bool IsHolidayThemed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return _holidayMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static int Preference(TrackCandidate track)
    {
        var haystack = track.Title + " " + track.Album;
        var penalty = _variantMarkers.Count(m => haystack.Contains(m, StringComparison.OrdinalIgnoreCase)) * 10;

        // Among equals, prefer the earliest release — usually the original studio cut.
        return penalty + (track.Year is > 1900 ? track.Year.Value - 1900 : 200);
    }

    private static string SongKey(TrackCandidate track)
    {
        var title = track.Title;

        // Drop parenthesised/bracketed suffixes: "Song (Live at Wembley)" == "Song".
        var cut = title.IndexOfAny(['(', '[']);
        if (cut > 2)
        {
            title = title[..cut];
        }

        // Drop " - Live" style suffixes, but never split a hyphenated word.
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 2)
        {
            title = title[..dash];
        }

        var builder = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return LibraryIndex.Normalize(track.Artist) + "|" + builder.ToString();
    }

    /// <summary>
    /// What the gate produced, and what it threw away getting there.
    /// </summary>
    public sealed class GateResult
    {
        /// <summary>
        /// Gets or sets the tracks to write, in playback order.
        /// </summary>
        public List<TrackCandidate> Tracks { get; set; } = [];

        /// <summary>
        /// Gets or sets the number of distinct artists in the result.
        /// </summary>
        public int DistinctArtists { get; set; }

        /// <summary>
        /// Gets or sets how many holiday tracks were removed.
        /// </summary>
        public int DroppedHoliday { get; set; }

        /// <summary>
        /// Gets or sets how many tracks were removed for being too short or too long.
        /// </summary>
        public int DroppedLength { get; set; }

        /// <summary>
        /// Gets or sets how many alternate versions of an already-included song were removed.
        /// </summary>
        public int DroppedDuplicate { get; set; }

        /// <summary>
        /// Gets or sets how many tracks were removed by the per-artist cap.
        /// </summary>
        public int DroppedArtistCap { get; set; }

        /// <summary>
        /// Gets or sets how many tracks were removed by the per-album cap.
        /// </summary>
        public int DroppedAlbumCap { get; set; }

        /// <summary>
        /// Checks the result against the floor below which a playlist is not proper.
        /// </summary>
        /// <param name="config">The plugin configuration.</param>
        /// <param name="reason">Why it failed, when it does.</param>
        /// <returns>True when the playlist is worth writing.</returns>
        public bool IsProper(PluginConfiguration config, out string reason)
        {
            ArgumentNullException.ThrowIfNull(config);

            if (Tracks.Count < config.MinTrackCount)
            {
                reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "only {0} tracks survived, floor is {1}",
                    Tracks.Count,
                    config.MinTrackCount);
                return false;
            }

            if (DistinctArtists < config.MinArtistCount)
            {
                reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "only {0} distinct artists, floor is {1}",
                    DistinctArtists,
                    config.MinArtistCount);
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// A one-line summary of what the gate removed, for the activity log.
        /// </summary>
        /// <returns>The summary.</returns>
        public string Summary() => string.Format(
            CultureInfo.InvariantCulture,
            "{0} tracks · {1} artists · dropped {2} holiday, {3} off-length, {4} duplicate, {5} artist-cap, {6} album-cap",
            Tracks.Count,
            DistinctArtists,
            DroppedHoliday,
            DroppedLength,
            DroppedDuplicate,
            DroppedArtistCap,
            DroppedAlbumCap);
    }
}
