namespace Clawindex.Collector.Api.Otlp;

public static class AgentIdValidator
{
    // Case-insensitive placeholder values that must never be used as real agent ids.
    // The two GUID sentinels are the all-zero nil GUID and the 000...0001 test sentinel.
    private static readonly HashSet<string> Denylist = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent", "test", "null", "none", "unknown", "id", "default",
        "00000000-0000-0000-0000-000000000000",
        "00000000-0000-0000-0000-000000000001"
    };

    /// <summary>
    /// For ingestion: trims and validates a raw span attribute value.
    /// Returns the trimmed id if valid, null if the value is absent, too short, or on the denylist.
    /// </summary>
    public static string? TryNormalize(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        return trimmed.Length >= 8 && !Denylist.Contains(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// For the read endpoint: validates the {id} path parameter.
    /// Returns (null, trimmedId) on success, (errorMessage, null) on failure.
    /// Rules match the ingestion conformance floor so an id that could never be ingested
    /// cannot be queried.
    /// </summary>
    public static (string? Error, string? AgentId) ValidateQueryId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ("'id' must not be empty", null);

        var trimmed = id.Trim();

        if (trimmed.Length < 8)
            return ($"'id' must be at least 8 characters", null);

        if (Denylist.Contains(trimmed))
            return ($"'{trimmed}' is a reserved placeholder value", null);

        return (null, trimmed);
    }
}
