using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.AutoPlaylist.Curation;

/// <summary>
/// A small in-memory record of the current or last run, so the settings page can show
/// what the curator is doing without anyone reading the server log.
/// </summary>
public sealed class RunLog
{
    private const int MaxLines = 400;

    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>
    /// Gets or sets a value indicating whether a run is in progress.
    /// </summary>
    public bool Running { get; set; }

    /// <summary>
    /// Gets or sets what the run is doing right now.
    /// </summary>
    public string Step { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current progress, 0 to 100.
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
    /// Starts a fresh run record.
    /// </summary>
    public void Begin()
    {
        while (_lines.TryDequeue(out _))
        {
        }

        Running = true;
        Progress = 0;
        Step = "starting";
        LastError = null;
        StartedUtc = DateTime.UtcNow;
        FinishedUtc = null;
    }

    /// <summary>
    /// Closes the run record.
    /// </summary>
    /// <param name="error">The error that ended the run, if any.</param>
    public void End(string? error)
    {
        Running = false;
        Progress = 100;
        Step = error is null ? "done" : "failed";
        LastError = error;
        FinishedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Appends a timestamped line.
    /// </summary>
    /// <param name="message">The message.</param>
    public void Add(string message)
    {
        _lines.Enqueue(string.Format(
            CultureInfo.InvariantCulture,
            "{0:HH:mm:ss} {1}",
            DateTime.Now,
            message));

        while (_lines.Count > MaxLines && _lines.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// Gets the recorded lines, oldest first.
    /// </summary>
    /// <returns>The lines.</returns>
    public IReadOnlyList<string> Lines() => _lines.ToList();
}
