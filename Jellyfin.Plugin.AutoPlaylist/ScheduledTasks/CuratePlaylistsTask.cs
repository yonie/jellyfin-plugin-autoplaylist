using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AutoPlaylist.Curation;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoPlaylist.ScheduledTasks;

/// <summary>
/// The nightly run: refresh the recently-added playlist and add new curated playlists.
/// </summary>
public class CuratePlaylistsTask : IScheduledTask
{
    private readonly PlaylistCurator _curator;
    private readonly ILogger<CuratePlaylistsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CuratePlaylistsTask"/> class.
    /// </summary>
    /// <param name="curator">The curator.</param>
    /// <param name="logger">The logger.</param>
    public CuratePlaylistsTask(PlaylistCurator curator, ILogger<CuratePlaylistsTask> logger)
    {
        _curator = curator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Curate playlists";

    /// <inheritdoc />
    public string Key => "AutoPlaylistCurate";

    /// <inheritdoc />
    public string Description =>
        "Asks the configured Ollama model for a new playlist angle, picks the tracks from your "
        + "library, and writes the playlist. Also refreshes the recently-added playlist.";

    /// <inheritdoc />
    public string Category => "AutoPlaylist";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3.25).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(6).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            _logger.LogWarning(
                "AutoPlaylist: no model selected — open Dashboard → Plugins → AutoPlaylist and pick one");
            return;
        }

        await _curator.RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
