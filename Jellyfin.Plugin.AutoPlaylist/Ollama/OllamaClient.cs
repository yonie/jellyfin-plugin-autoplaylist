using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AutoPlaylist.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoPlaylist.Ollama;

/// <summary>
/// Minimal Ollama client: model listing plus a single-shot chat call that is expected
/// to answer with JSON matching a schema.
/// </summary>
public class OllamaClient
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OllamaClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public OllamaClient(IHttpClientFactory httpClientFactory, ILogger<OllamaClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new OllamaException("AutoPlaylist is not initialised.");

    /// <summary>
    /// Lists the models the configured Ollama server has available.
    /// </summary>
    /// <param name="baseUrl">Optional override for the configured URL (used by the settings page).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The model names, sorted.</returns>
    public async Task<IReadOnlyList<string>> GetModelsAsync(string? baseUrl, CancellationToken cancellationToken)
    {
        var url = Combine(string.IsNullOrWhiteSpace(baseUrl) ? Config.OllamaUrl : baseUrl!, "api/tags");
        using var client = CreateClient(TimeSpan.FromSeconds(30));

        JsonNode? root;
        try
        {
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new OllamaException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Ollama returned {0} for {1}: {2}",
                        (int)response.StatusCode,
                        url,
                        Trim(body, 300)));
            }

            root = JsonNode.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new OllamaException($"Could not reach Ollama at {url}: {ex.Message}", ex);
        }

        var names = new List<string>();
        if (root?["models"] is JsonArray models)
        {
            foreach (var model in models)
            {
                var name = model?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name!);
                }
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Asks the model for a JSON answer and deserialises it.
    /// </summary>
    /// <typeparam name="T">The expected reply shape.</typeparam>
    /// <param name="systemPrompt">The system prompt.</param>
    /// <param name="userPrompt">The user prompt.</param>
    /// <param name="jsonSchema">A JSON schema describing the expected reply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deserialised reply.</returns>
    public async Task<T> ChatJsonAsync<T>(
        string systemPrompt,
        string userPrompt,
        string jsonSchema,
        CancellationToken cancellationToken)
        where T : class
    {
        var config = Config;
        if (string.IsNullOrWhiteSpace(config.Model))
        {
            throw new OllamaException("No Ollama model is selected in the AutoPlaylist settings.");
        }

        // Structured output first; then plain JSON mode; then no constraint at all.
        // Small models vary in what they honour, and a refused "format" must not end the run.
        var formats = new JsonNode?[] { JsonNode.Parse(jsonSchema), JsonValue.Create("json"), null };
        Exception? last = null;

        for (var attempt = 0; attempt < formats.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var content = await ChatAsync(config, systemPrompt, userPrompt, formats[attempt], cancellationToken)
                    .ConfigureAwait(false);
                var json = ExtractJson(content);
                if (json is null)
                {
                    throw new OllamaException("Model reply contained no JSON object.");
                }

                var result = JsonSerializer.Deserialize<T>(json, _jsonOptions);
                if (result is null)
                {
                    throw new OllamaException("Model reply deserialised to nothing.");
                }

                return result;
            }
            catch (Exception ex) when (ex is OllamaException or JsonException)
            {
                last = ex;
                _logger.LogWarning(
                    "AutoPlaylist: Ollama attempt {Attempt} of {Total} failed: {Message}",
                    attempt + 1,
                    formats.Length,
                    ex.Message);
            }
        }

        throw new OllamaException(
            "Ollama did not return usable JSON after three attempts. " +
            "Try a different model. Last error: " + (last?.Message ?? "unknown"),
            last!);
    }

    private static string Combine(string baseUrl, string path)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            trimmed = "http://localhost:11434";
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed + "/" + path;
    }

    private static string Trim(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// Pulls the first balanced JSON object out of a model reply, tolerating
    /// reasoning blocks, code fences and stray prose.
    /// </summary>
    /// <param name="content">The raw reply.</param>
    /// <returns>The JSON text, or null.</returns>
    internal static string? ExtractJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var text = content!;

        // Drop <think>…</think> blocks (and an unterminated trailing one).
        var thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        while (thinkStart >= 0)
        {
            var thinkEnd = text.IndexOf("</think>", thinkStart, StringComparison.OrdinalIgnoreCase);
            text = thinkEnd < 0
                ? text[..thinkStart]
                : text[..thinkStart] + text[(thinkEnd + "</think>".Length)..];
            thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return null;
    }

    private HttpClient CreateClient(TimeSpan timeout)
    {
        var client = _httpClientFactory.CreateClient("AutoPlaylist");
        client.Timeout = timeout;
        return client;
    }

    private async Task<string> ChatAsync(
        PluginConfiguration config,
        string systemPrompt,
        string userPrompt,
        JsonNode? format,
        CancellationToken cancellationToken)
    {
        var options = new JsonObject
        {
            ["temperature"] = config.Temperature
        };

        if (config.ContextTokens > 0)
        {
            options["num_ctx"] = config.ContextTokens;
        }

        var request = new JsonObject
        {
            ["model"] = config.Model,
            ["stream"] = false,
            ["keep_alive"] = string.IsNullOrWhiteSpace(config.KeepAlive) ? "10m" : config.KeepAlive,
            ["options"] = options,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            }
        };

        if (format is not null)
        {
            request["format"] = format.DeepClone();
        }

        if (config.DisableThinking)
        {
            request["think"] = false;
        }

        var url = Combine(config.OllamaUrl, "api/chat");
        using var client = CreateClient(TimeSpan.FromSeconds(Math.Max(30, config.RequestTimeoutSeconds)));

        if (config.VerboseLogging)
        {
            _logger.LogInformation("AutoPlaylist prompt to {Url}:\n{Prompt}", url, userPrompt);
        }

        string body;
        try
        {
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new OllamaException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Ollama returned {0}: {1}",
                        (int)response.StatusCode,
                        Trim(body, 400)));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new OllamaException($"Ollama request to {url} failed: {ex.Message}", ex);
        }

        var reply = JsonNode.Parse(body)?["message"]?["content"]?.GetValue<string>();
        if (config.VerboseLogging)
        {
            _logger.LogInformation("AutoPlaylist reply:\n{Reply}", Trim(reply ?? string.Empty, 4000));
        }

        return reply ?? string.Empty;
    }
}
