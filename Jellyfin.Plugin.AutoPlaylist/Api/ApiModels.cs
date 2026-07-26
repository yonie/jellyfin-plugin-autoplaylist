using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AutoPlaylist.Api;

/// <summary>
/// What the settings page shows about the current or last run.
/// </summary>
public class RunStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether a run is in progress.
    /// </summary>
    public bool Running { get; set; }

    /// <summary>
    /// Gets or sets what the run is doing right now.
    /// </summary>
    public string Step { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the progress, 0 to 100.
    /// </summary>
    public double Progress { get; set; }

    /// <summary>
    /// Gets or sets when the current or last run started.
    /// </summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the last run finished.
    /// </summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>
    /// Gets or sets the error that ended the last run, if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the activity log of the current or last run.
    /// </summary>
    public IReadOnlyList<string> Log { get; set; } = [];

    /// <summary>
    /// Gets or sets the playlists this plugin owns.
    /// </summary>
    public IReadOnlyList<OwnedPlaylist> Playlists { get; set; } = [];
}

/// <summary>
/// One playlist carrying the plugin's tag.
/// </summary>
public class OwnedPlaylist
{
    /// <summary>
    /// Gets or sets the playlist id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the playlist name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the angle description stored in the playlist overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of tracks in the playlist.
    /// </summary>
    public int TrackCount { get; set; }
}
