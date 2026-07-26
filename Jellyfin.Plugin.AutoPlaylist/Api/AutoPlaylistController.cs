using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AutoPlaylist.Curation;
using Jellyfin.Plugin.AutoPlaylist.Ollama;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AutoPlaylist.Api;

/// <summary>
/// Admin-only endpoints behind the settings page: check the Ollama connection, list
/// models, start or cancel a run, and manage the playlists the plugin owns.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("AutoPlaylist")]
[Produces(MediaTypeNames.Application.Json)]
public class AutoPlaylistController : ControllerBase
{
    private readonly PlaylistCurator _curator;
    private readonly OllamaClient _ollama;
    private readonly RunLog _runLog;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoPlaylistController"/> class.
    /// </summary>
    /// <param name="curator">The curator.</param>
    /// <param name="ollama">The Ollama client.</param>
    /// <param name="runLog">The run log.</param>
    public AutoPlaylistController(PlaylistCurator curator, OllamaClient ollama, RunLog runLog)
    {
        _curator = curator;
        _ollama = ollama;
        _runLog = runLog;
    }

    /// <summary>
    /// Lists the models available on the Ollama server.
    /// </summary>
    /// <param name="url">Optional URL to test instead of the saved one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The model names.</returns>
    [HttpGet("Models")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyList<string>>> GetModels(
        [FromQuery] string? url,
        CancellationToken cancellationToken)
    {
        try
        {
            var models = await _ollama.GetModelsAsync(url, cancellationToken).ConfigureAwait(false);
            return Ok(models);
        }
        catch (OllamaException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Gets the state of the current or last run, plus the playlists the plugin owns.
    /// </summary>
    /// <returns>The run status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RunStatus> GetStatus()
    {
        var playlists = new List<OwnedPlaylist>();
        try
        {
            playlists.AddRange(_curator.GetOwnedPlaylists().Select(p => new OwnedPlaylist
            {
                Id = p.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
                Name = p.Name ?? string.Empty,
                Overview = p.Overview ?? string.Empty,
                TrackCount = p.LinkedChildren.Length
            }));
        }
        catch (InvalidOperationException)
        {
            // No users yet, or the plugin is not initialised: report an empty list.
        }

        return Ok(new RunStatus
        {
            Running = _runLog.Running,
            Step = _runLog.Step,
            Progress = _runLog.Progress,
            StartedUtc = _runLog.StartedUtc,
            FinishedUtc = _runLog.FinishedUtc,
            LastError = _runLog.LastError,
            Log = _runLog.Lines(),
            Playlists = playlists
        });
    }

    /// <summary>
    /// Starts a curation run in the background.
    /// </summary>
    /// <returns>Accepted, or conflict when a run is already going.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult StartRun()
    {
        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.Model))
        {
            return BadRequest(new { Error = "Pick an Ollama model first." });
        }

        return _curator.StartBackgroundRun()
            ? Accepted()
            : Conflict(new { Error = "A curation run is already in progress." });
    }

    /// <summary>
    /// Writes descriptions for the plugin's own playlists that do not have one, reading a
    /// sample of each playlist to describe what is actually on it.
    /// </summary>
    /// <param name="overwrite">Also rewrite descriptions that already exist.</param>
    /// <returns>Accepted, or conflict when a run is already going.</returns>
    [HttpPost("Describe")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult WriteDescriptions([FromQuery] bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.Model))
        {
            return BadRequest(new { Error = "Pick an Ollama model first." });
        }

        return _curator.StartBackgroundDescribe(overwrite)
            ? Accepted()
            : Conflict(new { Error = "A curation run is already in progress." });
    }

    /// <summary>
    /// Cancels the run in progress.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult CancelRun()
    {
        _curator.CancelBackgroundRun();
        return NoContent();
    }

    /// <summary>
    /// Deletes one of the plugin's own playlists. Playlists without the plugin's tag
    /// are refused.
    /// </summary>
    /// <param name="playlistId">The playlist id.</param>
    /// <returns>No content, or not found.</returns>
    [HttpDelete("Playlists/{playlistId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeletePlaylist([FromRoute] Guid playlistId)
    {
        return _curator.DeleteOwnedPlaylist(playlistId)
            ? NoContent()
            : NotFound(new { Error = "That playlist is not one of AutoPlaylist's." });
    }
}
