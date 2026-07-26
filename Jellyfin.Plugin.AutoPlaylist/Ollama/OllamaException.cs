using System;

namespace Jellyfin.Plugin.AutoPlaylist.Ollama;

/// <summary>
/// Raised when the configured Ollama server cannot be reached or answers unusably.
/// </summary>
public class OllamaException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaException"/> class.
    /// </summary>
    public OllamaException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public OllamaException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public OllamaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
