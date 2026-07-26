using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.AutoPlaylist.Curation;

/// <summary>
/// A single library track, flattened into just what the curator needs to see.
/// </summary>
public sealed class TrackCandidate
{
    /// <summary>
    /// Gets or sets the Jellyfin item id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the track title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary artist, used for diversity caps.
    /// </summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets every credited artist.
    /// </summary>
    public string[] Artists { get; set; } = [];

    /// <summary>
    /// Gets or sets the album name.
    /// </summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the runtime in seconds.
    /// </summary>
    public int Seconds { get; set; }

    /// <summary>
    /// Gets or sets the genre tags.
    /// </summary>
    public string[] Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets when the track was added to the library.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets the album key used for per-album caps.
    /// </summary>
    public string AlbumKey => Album.Length == 0
        ? "single:" + Id.ToString("N", CultureInfo.InvariantCulture)
        : Artist.ToUpperInvariant() + "|" + Album.ToUpperInvariant();

    /// <summary>
    /// Renders the track as one compact prompt line.
    /// </summary>
    /// <param name="number">The number the model should answer with.</param>
    /// <returns>The formatted line.</returns>
    public string ToPromptLine(int number)
    {
        var builder = new StringBuilder(96);
        builder.Append(number.ToString(CultureInfo.InvariantCulture))
            .Append(". ")
            .Append(Artist)
            .Append(" — ")
            .Append(Title)
            .Append(" [");

        if (Album.Length > 0)
        {
            builder.Append(Album).Append(", ");
        }

        if (Year.HasValue)
        {
            builder.Append(Year.Value.ToString(CultureInfo.InvariantCulture)).Append(", ");
        }

        builder.Append((Seconds / 60).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append((Seconds % 60).ToString("00", CultureInfo.InvariantCulture));

        if (Genres.Length > 0)
        {
            builder.Append(", ").Append(Genres[0]);
        }

        return builder.Append(']').ToString();
    }
}
