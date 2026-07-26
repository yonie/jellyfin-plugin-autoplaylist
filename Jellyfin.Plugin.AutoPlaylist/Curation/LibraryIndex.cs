using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AutoPlaylist.Curation;

/// <summary>
/// An in-memory view of the music library: what artists, genres, decades and albums
/// actually exist. Built once per run so the curator can be shown real material
/// instead of guessing.
/// </summary>
public sealed class LibraryIndex
{
    private readonly Dictionary<string, List<TrackCandidate>> _byArtist = new(StringComparer.OrdinalIgnoreCase);

    private LibraryIndex(List<TrackCandidate> tracks)
    {
        Tracks = tracks;

        var artistCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var decadeCounts = new Dictionary<int, int>();
        var albums = new Dictionary<string, AlbumEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            artistCounts.TryGetValue(track.Artist, out var artistCount);
            artistCounts[track.Artist] = artistCount + 1;

            foreach (var credited in track.Artists)
            {
                var key = Normalize(credited);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!_byArtist.TryGetValue(key, out var list))
                {
                    list = [];
                    _byArtist[key] = list;
                }

                list.Add(track);
            }

            foreach (var genre in track.Genres)
            {
                genreCounts.TryGetValue(genre, out var genreCount);
                genreCounts[genre] = genreCount + 1;
            }

            if (track.Year is > 1900)
            {
                var decade = track.Year.Value / 10 * 10;
                decadeCounts.TryGetValue(decade, out var decadeCount);
                decadeCounts[decade] = decadeCount + 1;
            }

            if (!albums.TryGetValue(track.AlbumKey, out var album))
            {
                album = new AlbumEntry(track.Album, track.Artist);
                albums[track.AlbumKey] = album;
            }

            album.Tracks.Add(track);
            if (track.DateCreated > album.AddedUtc)
            {
                album.AddedUtc = track.DateCreated;
            }
        }

        ArtistsByCount = artistCounts.OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Key, x.Value))
            .ToList();
        GenresByCount = genreCounts.OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Key, x.Value))
            .ToList();
        Decades = decadeCounts.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToList();
        AlbumsByRecency = albums.Values.OrderByDescending(x => x.AddedUtc).ToList();
    }

    /// <summary>
    /// Gets every audio track visible to the curating user.
    /// </summary>
    public IReadOnlyList<TrackCandidate> Tracks { get; }

    /// <summary>
    /// Gets the artists present, most tracks first.
    /// </summary>
    public IReadOnlyList<(string Artist, int Count)> ArtistsByCount { get; }

    /// <summary>
    /// Gets the genre tags present, most tracks first.
    /// </summary>
    public IReadOnlyList<(string Genre, int Count)> GenresByCount { get; }

    /// <summary>
    /// Gets the track count per decade, oldest first.
    /// </summary>
    public IReadOnlyList<(int Decade, int Count)> Decades { get; }

    /// <summary>
    /// Gets the albums, most recently added first.
    /// </summary>
    public IReadOnlyList<AlbumEntry> AlbumsByRecency { get; }

    /// <summary>
    /// Reads the library for one user.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="user">The user whose library visibility applies.</param>
    /// <returns>The index.</returns>
    public static LibraryIndex Build(ILibraryManager libraryManager, User user)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            DtoOptions = new DtoOptions(false) { EnableImages = false }
        };

        var tracks = new List<TrackCandidate>();
        foreach (var item in libraryManager.GetItemList(query))
        {
            if (item is not Audio audio || string.IsNullOrWhiteSpace(audio.Name))
            {
                continue;
            }

            var artists = (audio.Artists ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
            if (artists.Length == 0)
            {
                artists = (audio.AlbumArtists ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
            }

            tracks.Add(new TrackCandidate
            {
                Id = audio.Id,
                Title = audio.Name.Trim(),
                Artist = artists.Length > 0 ? artists[0].Trim() : "Unknown Artist",
                Artists = artists,
                Album = (audio.Album ?? string.Empty).Trim(),
                Year = audio.ProductionYear,
                Seconds = audio.RunTimeTicks.HasValue
                    ? (int)TimeSpan.FromTicks(audio.RunTimeTicks.Value).TotalSeconds
                    : 0,
                Genres = audio.Genres ?? [],
                DateCreated = audio.DateCreated
            });
        }

        return new LibraryIndex(tracks);
    }

    /// <summary>
    /// Normalises an artist name for lookup ("The Beatles" and "beatles" agree).
    /// </summary>
    /// <param name="value">The name.</param>
    /// <returns>The lookup key.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[4..];
        }

        var builder = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Finds the tracks credited to an artist the model named. Exact first, then a
    /// loose containment match so near-misses in spelling still resolve.
    /// </summary>
    /// <param name="name">The artist name from the model.</param>
    /// <returns>The tracks, or an empty list.</returns>
    public IReadOnlyList<TrackCandidate> FindByArtist(string? name)
    {
        var key = Normalize(name);
        if (key.Length == 0)
        {
            return [];
        }

        if (_byArtist.TryGetValue(key, out var exact))
        {
            return exact;
        }

        foreach (var pair in _byArtist)
        {
            if (pair.Key.Length >= 4
                && (pair.Key.Contains(key, StringComparison.Ordinal)
                    || key.Contains(pair.Key, StringComparison.Ordinal)))
            {
                return pair.Value;
            }
        }

        return [];
    }

    /// <summary>
    /// Finds tracks whose title, album or genre mentions a term. This only assembles
    /// candidates for the model to judge — a text match never puts a track in a playlist.
    /// </summary>
    /// <param name="term">The search term.</param>
    /// <param name="limit">The maximum number of tracks to return.</param>
    /// <returns>The matching tracks.</returns>
    public IReadOnlyList<TrackCandidate> FindByTerm(string? term, int limit)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var needle = term.Trim();
        var hits = new List<TrackCandidate>();
        foreach (var track in Tracks)
        {
            if (track.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || track.Album.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || track.Genres.Any(g => g.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(track);
                if (hits.Count >= limit)
                {
                    break;
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Renders the "what does this library hold" briefing for the angle prompt.
    /// </summary>
    /// <param name="maxArtists">How many artists to list.</param>
    /// <param name="maxGenres">How many genres to list.</param>
    /// <returns>The snapshot text.</returns>
    public string BuildSnapshot(int maxArtists, int maxGenres)
    {
        var builder = new StringBuilder(4096);
        builder.Append(CultureInfo.InvariantCulture, $"Library: {Tracks.Count} tracks · ")
            .Append(CultureInfo.InvariantCulture, $"{ArtistsByCount.Count} artists · ")
            .Append(CultureInfo.InvariantCulture, $"{AlbumsByRecency.Count} albums")
            .AppendLine();

        builder.AppendLine().Append("Genres present: ");
        builder.AppendJoin(
            " · ",
            GenresByCount.Take(Math.Max(5, maxGenres)).Select(g => $"{g.Genre} {g.Count}"));
        builder.AppendLine();

        builder.AppendLine().Append("Decades: ");
        builder.AppendJoin(
            " · ",
            Decades.Where(d => d.Count > 5).Select(d => $"{d.Decade}s {d.Count}"));
        builder.AppendLine();

        builder.AppendLine().AppendLine("Artists present (track count) — curate around these, they exist:");
        builder.AppendJoin(
            " · ",
            ArtistsByCount.Take(Math.Max(20, maxArtists)).Select(a => $"{a.Artist} {a.Count}"));
        builder.AppendLine();

        return builder.ToString();
    }

    /// <summary>
    /// One album's tracks plus when it landed in the library.
    /// </summary>
    public sealed class AlbumEntry
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AlbumEntry"/> class.
        /// </summary>
        /// <param name="name">The album name.</param>
        /// <param name="artist">The album's primary artist.</param>
        public AlbumEntry(string name, string artist)
        {
            Name = name;
            Artist = artist;
        }

        /// <summary>
        /// Gets the album name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the album's primary artist.
        /// </summary>
        public string Artist { get; }

        /// <summary>
        /// Gets the album's tracks.
        /// </summary>
        public List<TrackCandidate> Tracks { get; } = [];

        /// <summary>
        /// Gets or sets when the album was added to the library.
        /// </summary>
        public DateTime AddedUtc { get; set; }
    }
}
