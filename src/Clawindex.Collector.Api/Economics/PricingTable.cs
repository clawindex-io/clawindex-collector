using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clawindex.Collector.Api.Economics;

public sealed class PricingTable
{
    private const int StaleDays = 90;

    private readonly IReadOnlyDictionary<(string Provider, string Model), (PricingEntry? Input, PricingEntry? Output)> _entries;

    public PricingTable(Stream json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var doc = JsonSerializer.Deserialize<PricingDocument>(json, options)
            ?? throw new InvalidOperationException("pricing.json is empty or invalid");

        _entries = doc.Entries
            .GroupBy(e => (e.Provider.ToLowerInvariant(), e.Model.ToLowerInvariant()))
            .ToDictionary(
                g => g.Key,
                g => (
                    Input:  g.FirstOrDefault(e => e.TokenType.Equals("input",  StringComparison.OrdinalIgnoreCase)),
                    Output: g.FirstOrDefault(e => e.TokenType.Equals("output", StringComparison.OrdinalIgnoreCase))
                ));
    }

    // v1: spanTimestamp is accepted for API stability but ignored — flat (always latest row).
    // Future: filter to entries where effective_from <= spanTimestamp, take latest.
    public PriceResolution? TryResolvePrice(string? provider, string? model, DateTimeOffset spanTimestamp)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            return null;

        var key = (provider.ToLowerInvariant(), model.ToLowerInvariant());
        if (!_entries.TryGetValue(key, out var pair) || pair.Input is null || pair.Output is null)
            return null;

        var pricedAsOf = pair.Input.LastConfirmed < pair.Output.LastConfirmed
            ? pair.Input.LastConfirmed
            : pair.Output.LastConfirmed;

        return new PriceResolution(
            InputPricePerToken:  pair.Input.PricePerToken,
            OutputPricePerToken: pair.Output.PricePerToken,
            PricedAsOf:          pricedAsOf,
            IsStale:             pricedAsOf.AddDays(StaleDays) < DateOnly.FromDateTime(DateTime.UtcNow));
    }

    private sealed class PricingDocument
    {
        [JsonPropertyName("entries")]
        public List<PricingEntry> Entries { get; set; } = [];
    }
}
