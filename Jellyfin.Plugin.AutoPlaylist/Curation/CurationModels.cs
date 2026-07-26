using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoPlaylist.Curation;

/// <summary>
/// The model's proposal for one new playlist.
/// </summary>
public sealed class AngleProposal
{
    /// <summary>
    /// Gets or sets the playlist name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the one-line description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the artists whose catalogues should be searched.
    /// </summary>
    [JsonPropertyName("seed_artists")]
    public List<string>? SeedArtists { get; set; }

    /// <summary>
    /// Gets or sets extra terms to scan titles, albums and genres for.
    /// </summary>
    [JsonPropertyName("search_terms")]
    public List<string>? SearchTerms { get; set; }

    /// <summary>
    /// Gets or sets the model's justification.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
}

/// <summary>
/// The model's description for an existing playlist.
/// </summary>
public sealed class DescriptionResponse
{
    /// <summary>
    /// Gets or sets the written description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// The model's picks from one candidate batch.
/// </summary>
public sealed class PickResponse
{
    /// <summary>
    /// Gets or sets the raw picks, as whatever JSON the model produced.
    /// </summary>
    [JsonPropertyName("picks")]
    public List<JsonElement>? Picks { get; set; }

    /// <summary>
    /// Reads the picks as integers, tolerating models that quote their numbers.
    /// </summary>
    /// <returns>The pick numbers.</returns>
    public IReadOnlyList<int> Numbers()
    {
        var numbers = new List<int>();
        if (Picks is null)
        {
            return numbers;
        }

        foreach (var element in Picks)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number when element.TryGetInt32(out var value):
                    numbers.Add(value);
                    break;
                case JsonValueKind.String
                    when int.TryParse(
                        element.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsed):
                    numbers.Add(parsed);
                    break;
                case JsonValueKind.Object when element.TryGetProperty("number", out var inner)
                                               && inner.TryGetInt32(out var nested):
                    numbers.Add(nested);
                    break;
                default:
                    break;
            }
        }

        return numbers;
    }
}
