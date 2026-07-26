using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoPlaylist.Configuration;

/// <summary>
/// AutoPlaylist settings. Everything the curator needs lives here; the only external
/// dependency is the Ollama server at <see cref="OllamaUrl"/>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base URL of the Ollama server.
    /// </summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Gets or sets the Ollama model used for curation.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sampling temperature.
    /// </summary>
    public double Temperature { get; set; } = 0.8;

    /// <summary>
    /// Gets or sets the context window in tokens. Zero uses the model default.
    /// </summary>
    public int ContextTokens { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to explicitly disable model "thinking"
    /// (faster, and enough for this task on small local models).
    /// </summary>
    public bool DisableThinking { get; set; }

    /// <summary>
    /// Gets or sets the per-request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Gets or sets how long Ollama should keep the model loaded between calls.
    /// </summary>
    public string KeepAlive { get; set; } = "10m";

    /// <summary>
    /// Gets or sets the id of the user who owns the generated playlists.
    /// Empty means "the first administrator".
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many new playlists a single run may create.
    /// </summary>
    public int PlaylistsPerRun { get; set; } = 1;

    /// <summary>
    /// Gets or sets the total number of managed playlists to stop at. Zero means no limit.
    /// </summary>
    public int MaxManagedPlaylists { get; set; }

    /// <summary>
    /// Gets or sets the desired playlist length in tracks.
    /// </summary>
    public int TargetTrackCount { get; set; } = 200;

    /// <summary>
    /// Gets or sets the maximum number of tracks by one artist in a playlist.
    /// </summary>
    public int MaxTracksPerArtist { get; set; } = 8;

    /// <summary>
    /// Gets or sets the maximum number of tracks from one album in a playlist.
    /// </summary>
    public int MaxTracksPerAlbum { get; set; } = 5;

    /// <summary>
    /// Gets or sets the minimum number of tracks for a playlist to be considered proper.
    /// Below this the angle is dropped instead of padded.
    /// </summary>
    public int MinTrackCount { get; set; } = 50;

    /// <summary>
    /// Gets or sets the minimum number of distinct artists for a playlist to be
    /// considered proper.
    /// </summary>
    public int MinArtistCount { get; set; } = 15;

    /// <summary>
    /// Gets or sets the shortest track length to include, in seconds.
    /// </summary>
    public int MinTrackSeconds { get; set; } = 100;

    /// <summary>
    /// Gets or sets the longest track length to include, in seconds.
    /// </summary>
    public int MaxTrackSeconds { get; set; } = 420;

    /// <summary>
    /// Gets or sets a value indicating whether holiday music is excluded from
    /// playlists that are not explicitly about it.
    /// </summary>
    public bool ExcludeHolidayMusic { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether live/remix/karaoke duplicates are
    /// collapsed in favour of the studio version.
    /// </summary>
    public bool PreferStudioVersions { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether created playlists are public.
    /// </summary>
    public bool MakePlaylistsPublic { get; set; }

    /// <summary>
    /// Gets or sets an optional prefix added to every generated playlist name,
    /// so they are easy to spot among your own.
    /// </summary>
    public string PlaylistNamePrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to maintain one continuously refreshed
    /// playlist of recently added music.
    /// </summary>
    public bool MaintainFreshFinds { get; set; } = true;

    /// <summary>
    /// Gets or sets the name of the recently-added playlist.
    /// </summary>
    public string FreshFindsName { get; set; } = "Fresh Finds";

    /// <summary>
    /// Gets or sets how many of the most recently added albums feed the
    /// recently-added playlist.
    /// </summary>
    public int FreshFindsAlbumCount { get; set; } = 60;

    /// <summary>
    /// Gets or sets how many candidate tracks are shown to the model per request.
    /// </summary>
    public int CandidatesPerRequest { get; set; } = 80;

    /// <summary>
    /// Gets or sets the maximum size of the candidate pool for one playlist.
    /// </summary>
    public int MaxCandidatePool { get; set; } = 1200;

    /// <summary>
    /// Gets or sets how many tracks per seed artist enter the candidate pool.
    /// </summary>
    public int CandidatesPerSeedArtist { get; set; } = 14;

    /// <summary>
    /// Gets or sets how many artists are listed in the library snapshot prompt.
    /// </summary>
    public int MaxArtistsInPrompt { get; set; } = 150;

    /// <summary>
    /// Gets or sets how many angles to try before giving up on a run.
    /// </summary>
    public int AngleAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets extra curator instructions appended to the built-in prompt
    /// (your taste, languages to favour, things to avoid).
    /// </summary>
    public string ExtraInstructions { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether every prompt and reply is written to
    /// the Jellyfin log.
    /// </summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Gets or sets the tag that marks a playlist as owned by this plugin. Playlists
    /// carrying this tag are the only ones it will ever rebuild or delete; everything
    /// else in the server — including every playlist you made yourself — is off limits.
    /// Ownership lives in Jellyfin, so there is no state file to keep in sync.
    /// </summary>
    public string PlaylistTag { get; set; } = "jl-autoplaylist";

    /// <summary>
    /// Gets or sets the UTC time of the last completed run.
    /// </summary>
    public DateTime LastRunUtc { get; set; }
}
