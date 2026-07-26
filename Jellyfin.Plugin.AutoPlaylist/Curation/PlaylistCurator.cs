using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AutoPlaylist.Configuration;
using Jellyfin.Plugin.AutoPlaylist.Ollama;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoPlaylist.Curation;

/// <summary>
/// Runs the whole method end to end: look at the library, choose an angle, look through
/// real tracks, pick by judgment, enforce the quality bar, write the playlist.
/// </summary>
public sealed class PlaylistCurator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;
    private readonly OllamaClient _ollama;
    private readonly RunLog _runLog;
    private readonly ILogger<PlaylistCurator> _logger;
    private readonly Random _random = new();
    private CancellationTokenSource? _backgroundRun;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistCurator"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="playlistManager">The playlist manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="ollama">The Ollama client.</param>
    /// <param name="runLog">The run log.</param>
    /// <param name="logger">The logger.</param>
    public PlaylistCurator(
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager,
        IUserManager userManager,
        OllamaClient ollama,
        RunLog runLog,
        ILogger<PlaylistCurator> logger)
    {
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _userManager = userManager;
        _ollama = ollama;
        _runLog = runLog;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether a run is currently in progress.
    /// </summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("AutoPlaylist is not initialised.");

    /// <summary>
    /// Curates playlists. One run refreshes the recently-added playlist (if enabled) and
    /// adds up to <see cref="PluginConfiguration.PlaylistsPerRun"/> new themed playlists.
    /// </summary>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the run does.</returns>
    public async Task RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A curation run is already in progress.");
        }

        var config = Config;
        _runLog.Begin();
        try
        {
            var user = ResolveUser(config, true);
            Report(progress, 2, "reading library");

            var index = LibraryIndex.Build(_libraryManager, user);
            Log(string.Format(
                CultureInfo.InvariantCulture,
                "library: {0} tracks, {1} artists, {2} albums",
                index.Tracks.Count,
                index.ArtistsByCount.Count,
                index.AlbumsByRecency.Count));

            if (index.Tracks.Count < config.MinTrackCount * 2)
            {
                throw new InvalidOperationException(
                    "This library is too small to curate from: "
                    + index.Tracks.Count.ToString(CultureInfo.InvariantCulture) + " tracks.");
            }

            var owned = GetOwnedPlaylists(user.Id, config);
            Log(string.Format(
                CultureInfo.InvariantCulture,
                "{0} playlist(s) already tagged '{1}'",
                owned.Count,
                config.PlaylistTag));

            if (config.WriteMissingDescriptions)
            {
                Report(progress, 4, "writing missing descriptions");
                try
                {
                    await DescribeCoreAsync(config, false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OllamaException or InvalidOperationException)
                {
                    Log("writing descriptions failed: " + ex.Message);
                }
            }

            if (config.MaintainFreshFinds)
            {
                Report(progress, 5, "refreshing " + config.FreshFindsName);
                try
                {
                    await CurateFreshFindsAsync(index, user, owned, config, progress, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OllamaException or InvalidOperationException)
                {
                    Log(config.FreshFindsName + " failed: " + ex.Message);
                    _logger.LogWarning(ex, "AutoPlaylist: {Name} failed", config.FreshFindsName);
                }
            }

            var wanted = Math.Max(0, config.PlaylistsPerRun);
            for (var i = 0; i < wanted; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                owned = GetOwnedPlaylists(user.Id, config);
                if (config.MaxManagedPlaylists > 0 && owned.Count >= config.MaxManagedPlaylists)
                {
                    Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "stopping: {0} playlists is the configured maximum",
                        config.MaxManagedPlaylists));
                    break;
                }

                var basePercent = 10 + (i * 90.0 / wanted);
                var span = 90.0 / wanted;
                await CurateThemedAsync(index, user, owned, config, progress, basePercent, span, cancellationToken)
                    .ConfigureAwait(false);
            }

            config.LastRunUtc = DateTime.UtcNow;
            Plugin.Instance!.SaveConfiguration();
            Report(progress, 100, "done");
            _runLog.End(null);
        }
        catch (OperationCanceledException)
        {
            Log("cancelled");
            _runLog.End("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            Log("run failed: " + ex.Message);
            _logger.LogError(ex, "AutoPlaylist: run failed");
            _runLog.End(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes descriptions for playlists this plugin owns that do not have one, by
    /// reading a sample of what is on each and asking the model to describe it.
    /// </summary>
    /// <param name="overwriteExisting">Also rewrite descriptions that already exist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many descriptions were written.</returns>
    public async Task<int> DescribeOwnedPlaylistsAsync(bool overwriteExisting, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A curation run is already in progress.");
        }

        _runLog.Begin();
        try
        {
            var written = await DescribeCoreAsync(Config, overwriteExisting, cancellationToken)
                .ConfigureAwait(false);
            _runLog.End(null);
            return written;
        }
        catch (Exception ex)
        {
            Log("writing descriptions failed: " + ex.Message);
            _runLog.End(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts a run in the background, for the "curate now" button.
    /// </summary>
    /// <returns>False when a run is already in progress.</returns>
    public bool StartBackgroundRun()
    {
        return StartBackground(cts => RunAsync(null, cts));
    }

    /// <summary>
    /// Starts a description backfill in the background, for the settings page button.
    /// </summary>
    /// <param name="overwriteExisting">Also rewrite descriptions that already exist.</param>
    /// <returns>False when a run is already in progress.</returns>
    public bool StartBackgroundDescribe(bool overwriteExisting)
    {
        return StartBackground(cts => DescribeOwnedPlaylistsAsync(overwriteExisting, cts));
    }

    private bool StartBackground(Func<CancellationToken, Task> work)
    {
        if (IsRunning)
        {
            return false;
        }

        var cts = new CancellationTokenSource();
        _backgroundRun = cts;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await work(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AutoPlaylist: background work ended");
                }
                finally
                {
                    _backgroundRun = null;
                    cts.Dispose();
                }
            },
            CancellationToken.None);

        return true;
    }

    /// <summary>
    /// Cancels a run started by <see cref="StartBackgroundRun"/>.
    /// </summary>
    public void CancelBackgroundRun()
    {
        try
        {
            _backgroundRun?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished between the check and the cancel; nothing to do.
        }
    }

    /// <summary>
    /// Lists the playlists this plugin owns, for the settings page.
    /// </summary>
    /// <returns>The owned playlists.</returns>
    public IReadOnlyList<Playlist> GetOwnedPlaylists()
    {
        var config = Config;
        var user = ResolveUser(config, false);
        return GetOwnedPlaylists(user.Id, config);
    }

    /// <summary>
    /// Lists the playlists this plugin owns — the ones carrying its tag. Everything else
    /// in the server is off limits, whether or not the name looks familiar.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The owned playlists.</returns>
    public IReadOnlyList<Playlist> GetOwnedPlaylists(Guid userId, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var tag = string.IsNullOrWhiteSpace(config.PlaylistTag) ? "jl-autoplaylist" : config.PlaylistTag.Trim();
        return _playlistManager.GetPlaylists(userId)
            .Where(p => p.Tags is not null
                        && p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Deletes a playlist, but only if it carries this plugin's tag.
    /// </summary>
    /// <param name="playlistId">The playlist id.</param>
    /// <returns>True when it was deleted.</returns>
    public bool DeleteOwnedPlaylist(Guid playlistId)
    {
        var config = Config;
        var tag = string.IsNullOrWhiteSpace(config.PlaylistTag) ? "jl-autoplaylist" : config.PlaylistTag.Trim();
        var item = _libraryManager.GetItemById(playlistId);
        if (item is null
            || item.Tags is null
            || !item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
        Log("deleted " + item.Name);
        return true;
    }

    private static IReadOnlyList<List<TrackCandidate>> Chunk(IReadOnlyList<TrackCandidate> pool, int size)
    {
        var batches = new List<List<TrackCandidate>>();
        for (var i = 0; i < pool.Count; i += size)
        {
            batches.Add(pool.Skip(i).Take(size).ToList());
        }

        return batches;
    }

    private User ResolveUser(PluginConfiguration config, bool log = true)
    {
        if (!string.IsNullOrWhiteSpace(config.UserId)
            && Guid.TryParse(config.UserId, out var configured))
        {
            var user = _userManager.GetUserById(configured);
            if (user is not null)
            {
                return user;
            }

            if (log)
            {
                Log("configured user no longer exists, falling back to the first administrator");
            }
        }

        var users = _userManager.GetUsers().ToList();
        var admin = users.FirstOrDefault(u =>
            u.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value));

        var chosen = admin ?? users.FirstOrDefault()
            ?? throw new InvalidOperationException("This server has no users to own the playlists.");

        if (log)
        {
            Log("curating as " + chosen.Username);
        }

        return chosen;
    }

    private void Log(string message)
    {
        _runLog.Add(message);
        _logger.LogInformation("AutoPlaylist: {Message}", message);
    }

    private void Report(IProgress<double>? progress, double percent, string step)
    {
        _runLog.Step = step;
        _runLog.Progress = percent;
        progress?.Report(percent);
    }

    /// <summary>
    /// Writes the missing descriptions. Reads a sample of each playlist's real contents,
    /// so a playlist created before descriptions existed still gets an accurate one.
    /// </summary>
    private async Task<int> DescribeCoreAsync(
        PluginConfiguration config,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        var user = ResolveUser(config, false);
        var owned = GetOwnedPlaylists(user.Id, config);
        var system = CurationPrompts.SystemPrompt(config);
        var written = 0;

        foreach (var playlist in owned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!overwriteExisting && !string.IsNullOrWhiteSpace(playlist.Overview))
            {
                continue;
            }

            if (await DescribeOneAsync(playlist, system, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        Log(string.Format(CultureInfo.InvariantCulture, "wrote {0} description(s)", written));
        return written;
    }

    /// <summary>
    /// Describes one playlist from a sample of what is actually on it.
    /// </summary>
    private async Task<bool> DescribeOneAsync(
        Playlist playlist,
        string system,
        CancellationToken cancellationToken)
    {
        var children = playlist.GetLinkedChildren();
        if (children.Count < 5)
        {
            return false;
        }

        // An even sample across the playlist, so the description reflects the whole
        // thing rather than whatever happens to be at the top.
        var step = Math.Max(1, children.Count / 40);
        var lines = new List<string>(40);
        for (var i = 0; i < children.Count && lines.Count < 40; i += step)
        {
            if (children[i] is Audio audio)
            {
                var artist = audio.Artists is { Count: > 0 } ? audio.Artists[0] : "Unknown";
                lines.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} — {1} [{2}{3}]",
                    artist,
                    audio.Name,
                    audio.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "?",
                    audio.Genres is { Length: > 0 } ? ", " + audio.Genres[0] : string.Empty));
            }
        }

        if (lines.Count < 5)
        {
            return false;
        }

        _runLog.Step = "describing " + playlist.Name;
        try
        {
            var reply = await _ollama.ChatJsonAsync<DescriptionResponse>(
                system,
                CurationPrompts.DescriptionPrompt(playlist.Name, string.Join('\n', lines)),
                CurationPrompts.DescriptionSchema,
                cancellationToken).ConfigureAwait(false);

            var description = (reply.Description ?? string.Empty).Trim();
            if (description.Length == 0)
            {
                return false;
            }

            playlist.Overview = description;
            await playlist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
            Log("described \"" + playlist.Name + "\": " + description);
            return true;
        }
        catch (OllamaException ex)
        {
            Log("could not describe \"" + playlist.Name + "\": " + ex.Message);
            return false;
        }
    }

    private async Task CurateThemedAsync(
        LibraryIndex index,
        User user,
        IReadOnlyList<Playlist> owned,
        PluginConfiguration config,
        IProgress<double>? progress,
        double basePercent,
        double span,
        CancellationToken cancellationToken)
    {
        var existingNames = owned.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var rejected = new List<string>();
        var system = CurationPrompts.SystemPrompt(config);

        for (var attempt = 1; attempt <= Math.Max(1, config.AngleAttempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, basePercent, "choosing an angle");

            var proposal = await _ollama.ChatJsonAsync<AngleProposal>(
                system,
                CurationPrompts.AnglePrompt(index, existingNames, config, rejected),
                CurationPrompts.AngleSchema,
                cancellationToken).ConfigureAwait(false);

            var name = (proposal.Name ?? string.Empty).Trim();
            var description = (proposal.Description ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                rejected.Add("a proposal with no name");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(config.PlaylistNamePrefix))
            {
                name = config.PlaylistNamePrefix.Trim() + " " + name;
            }

            Log(string.Format(
                CultureInfo.InvariantCulture,
                "angle {0}/{1}: \"{2}\" — {3}",
                attempt,
                config.AngleAttempts,
                name,
                description));

            if (NameIsTaken(name, user.Id))
            {
                Log("a playlist called \"" + name + "\" already exists — asking for a different angle");
                rejected.Add(name + " (name already exists)");
                existingNames.Add(name);
                continue;
            }

            var pool = BuildPool(index, proposal, config);
            Log(string.Format(CultureInfo.InvariantCulture, "candidate pool: {0} tracks", pool.Count));
            if (pool.Count < config.MinTrackCount * 2)
            {
                Log("the library cannot support this angle — dropping it");
                rejected.Add(name + " (too few candidates in the library)");
                continue;
            }

            var picks = await SelectAsync(
                name,
                description,
                pool,
                config,
                system,
                progress,
                basePercent,
                span,
                cancellationToken).ConfigureAwait(false);

            var gate = QualityGate.Apply(
                picks,
                config,
                QualityGate.IsHolidayThemed(name + " " + description),
                _random);
            Log("after the quality bar: " + gate.Summary());

            if (!gate.IsProper(config, out var reason))
            {
                Log("not a proper playlist (" + reason + ") — dropping the idea");
                rejected.Add(name + " (" + reason + ")");
                continue;
            }

            var ids = gate.Tracks.Select(t => t.Id).ToList();
            var playlistId = await CreateTaggedPlaylistAsync(name, description, ids, user.Id, config, cancellationToken)
                .ConfigureAwait(false);

            Log(string.Format(
                CultureInfo.InvariantCulture,
                "created \"{0}\" with {1} tracks ({2})",
                name,
                ids.Count,
                playlistId.ToString("N", CultureInfo.InvariantCulture)));
            return;
        }

        Log("gave up on this playlist after " + config.AngleAttempts.ToString(CultureInfo.InvariantCulture)
            + " attempts");
    }

    private async Task CurateFreshFindsAsync(
        LibraryIndex index,
        User user,
        IReadOnlyList<Playlist> owned,
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(config.FreshFindsName) ? "Fresh Finds" : config.FreshFindsName.Trim();
        var displayName = string.IsNullOrWhiteSpace(config.PlaylistNamePrefix)
            ? name
            : config.PlaylistNamePrefix.Trim() + " " + name;

        var pool = index.AlbumsByRecency
            .Take(Math.Max(5, config.FreshFindsAlbumCount))
            .SelectMany(a => a.Tracks)
            .Take(config.MaxCandidatePool)
            .ToList();

        if (pool.Count < config.MinTrackCount)
        {
            Log(displayName + ": not enough recently added music yet");
            return;
        }

        var system = CurationPrompts.SystemPrompt(config);
        var picks = await SelectAsync(
            displayName,
            CurationPrompts.FreshFindsDescription,
            pool,
            config,
            system,
            progress,
            5,
            5,
            cancellationToken).ConfigureAwait(false);

        var gate = QualityGate.Apply(picks, config, false, _random);
        Log(displayName + " after the quality bar: " + gate.Summary());

        // Recency is a thin slice of the library, so accept a shorter list here rather
        // than dropping the playlist — but still refuse something threadbare.
        if (gate.Tracks.Count < Math.Min(config.MinTrackCount, 25))
        {
            Log(displayName + ": too thin to write");
            return;
        }

        var ids = gate.Tracks.Select(t => t.Id).ToList();
        var existing = owned.FirstOrDefault(p => string.Equals(p.Name, displayName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var created = await CreateTaggedPlaylistAsync(
                displayName,
                CurationPrompts.FreshFindsDescription,
                ids,
                user.Id,
                config,
                cancellationToken).ConfigureAwait(false);
            Log(string.Format(
                CultureInfo.InvariantCulture,
                "created \"{0}\" with {1} tracks ({2})",
                displayName,
                ids.Count,
                created.ToString("N", CultureInfo.InvariantCulture)));
            return;
        }

        // Replacing Ids clears the playlist and re-adds in this order, which is also the
        // playback order — the list is already shuffled.
        await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
        {
            Id = existing.Id,
            UserId = user.Id,
            Ids = ids
        }).ConfigureAwait(false);

        // Recency needs no written description — the playlist is what it says it is.
        await TagAsync(existing.Id, CurationPrompts.FreshFindsDescription, config, cancellationToken)
            .ConfigureAwait(false);
        Log(string.Format(
            CultureInfo.InvariantCulture,
            "refreshed \"{0}\" with {1} tracks",
            displayName,
            ids.Count));
    }

    private bool NameIsTaken(string name, Guid userId)
    {
        return _playlistManager.GetPlaylists(userId)
            .Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private List<TrackCandidate> BuildPool(LibraryIndex index, AngleProposal proposal, PluginConfiguration config)
    {
        var pool = new List<TrackCandidate>();
        var seen = new HashSet<Guid>();
        var unresolved = new List<string>();

        foreach (var seed in proposal.SeedArtists ?? [])
        {
            var tracks = index.FindByArtist(seed);
            if (tracks.Count == 0)
            {
                unresolved.Add(seed);
                continue;
            }

            // Spread the sample across the artist's catalogue instead of taking one album.
            foreach (var track in tracks
                         .OrderBy(_ => _random.Next())
                         .Take(Math.Max(1, config.CandidatesPerSeedArtist)))
            {
                if (seen.Add(track.Id))
                {
                    pool.Add(track);
                }
            }
        }

        foreach (var term in proposal.SearchTerms ?? [])
        {
            foreach (var track in index.FindByTerm(term, 60))
            {
                if (seen.Add(track.Id))
                {
                    pool.Add(track);
                }
            }
        }

        if (unresolved.Count > 0)
        {
            Log(string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1} seed artists are not in this library ({2})",
                unresolved.Count,
                (proposal.SeedArtists ?? []).Count,
                string.Join(", ", unresolved.Take(8))));
        }

        if (pool.Count > config.MaxCandidatePool)
        {
            pool = pool.OrderBy(_ => _random.Next()).Take(config.MaxCandidatePool).ToList();
        }
        else
        {
            pool = pool.OrderBy(_ => _random.Next()).ToList();
        }

        return pool;
    }

    private async Task<List<TrackCandidate>> SelectAsync(
        string name,
        string description,
        IReadOnlyList<TrackCandidate> pool,
        PluginConfiguration config,
        string system,
        IProgress<double>? progress,
        double basePercent,
        double span,
        CancellationToken cancellationToken)
    {
        var batches = Chunk(pool, Math.Max(20, config.CandidatesPerRequest));
        var picks = new List<TrackCandidate>();
        var chosen = new HashSet<Guid>();
        var ceiling = (int)(config.TargetTrackCount * 1.6);
        var failures = 0;

        for (var i = 0; i < batches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (picks.Count >= ceiling)
            {
                Log("enough candidates chosen, skipping the remaining batches");
                break;
            }

            Report(
                progress,
                basePercent + (span * (i + 1) / (batches.Count + 1)),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: batch {1}/{2}, {3} picked",
                    name,
                    i + 1,
                    batches.Count,
                    picks.Count));

            PickResponse reply;
            try
            {
                reply = await _ollama.ChatJsonAsync<PickResponse>(
                    system,
                    CurationPrompts.SelectionPrompt(
                        name,
                        description,
                        batches[i],
                        i + 1,
                        batches.Count,
                        picks.Count,
                        config),
                    CurationPrompts.PicksSchema,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OllamaException ex)
            {
                failures++;
                Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "batch {0} failed ({1})",
                    i + 1,
                    ex.Message));

                if (failures >= 3 && picks.Count == 0)
                {
                    throw;
                }

                continue;
            }

            var added = 0;
            foreach (var number in reply.Numbers())
            {
                if (number < 1 || number > batches[i].Count)
                {
                    continue;
                }

                var track = batches[i][number - 1];
                if (chosen.Add(track.Id))
                {
                    picks.Add(track);
                    added++;
                }
            }

            if (config.VerboseLogging)
            {
                Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "batch {0}/{1}: +{2}",
                    i + 1,
                    batches.Count,
                    added));
            }
        }

        Log(string.Format(CultureInfo.InvariantCulture, "{0}: model picked {1} tracks", name, picks.Count));
        return picks;
    }

    private async Task<Guid> CreateTaggedPlaylistAsync(
        string name,
        string description,
        IReadOnlyList<Guid> ids,
        Guid userId,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = name,
            ItemIdList = ids,
            MediaType = MediaType.Audio,
            UserId = userId,
            Public = config.MakePlaylistsPublic
        }).ConfigureAwait(false);

        var playlistId = Guid.Parse(result.Id);
        await TagAsync(playlistId, description, config, cancellationToken).ConfigureAwait(false);
        return playlistId;
    }

    /// <summary>
    /// Marks a playlist as ours. Creation does not accept tags, so this is a required
    /// follow-up write — without it the playlist would be invisible to later runs.
    /// </summary>
    private async Task TagAsync(
        Guid playlistId,
        string description,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var tag = string.IsNullOrWhiteSpace(config.PlaylistTag) ? "jl-autoplaylist" : config.PlaylistTag.Trim();
        var item = _libraryManager.GetItemById(playlistId);
        if (item is null)
        {
            Log("could not re-read the new playlist to tag it");
            return;
        }

        var tags = (item.Tags ?? []).ToList();
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(tag);
        }

        item.Tags = tags.ToArray();
        if (!string.IsNullOrWhiteSpace(description))
        {
            item.Overview = description;
        }

        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }
}
