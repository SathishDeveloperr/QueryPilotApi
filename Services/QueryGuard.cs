using System.Text.Json;

namespace QueryPilot.Api.Services;

/// <summary>
/// SECURITY LAYER - the most important part of this project.
///
/// The LLM writes MongoDB aggregation pipelines from user text. We must treat that
/// output as UNTRUSTED (an attacker could ask "delete all customers"). Defence:
///   1. Whitelist which collections may be queried (config: Query:AllowedCollections)
///   2. Blacklist pipeline stages/operators that write data or execute code
///   3. Force a $limit so a bad query can't return millions of rows
/// This mirrors how you'd guard LLM-generated SQL (interview gold).
/// </summary>
public class QueryGuard
{
    private readonly HashSet<string> _allowedCollections;
    private readonly int _maxResults;

    // Stages/operators that can write, delete, or execute code - never allowed.
    private static readonly string[] Forbidden =
    {
        "$out", "$merge", "$function", "$where", "$accumulator",
        "$graphLookup", "$currentOp", "$listSessions", "$planCacheStats",
    };

    public QueryGuard(IConfiguration config)
    {
        _allowedCollections = config.GetSection("Query:AllowedCollections")
            .Get<string[]>()?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
        _maxResults = config.GetValue("Query:MaxResults", 50);
    }

    public int MaxResults => _maxResults;

    /// <summary>Returns null if safe, otherwise the reason the query was blocked.</summary>
    public string? Validate(string collection, string pipelineJson)
    {
        if (!_allowedCollections.Contains(collection))
            return $"Collection '{collection}' is not in the allowed list.";

        // Must be a JSON array of stages.
        JsonDocument doc;
        try { doc = JsonDocument.Parse(pipelineJson); }
        catch (JsonException) { return "Generated pipeline is not valid JSON."; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "Pipeline must be a JSON array of aggregation stages.";

            // Walk every property name in the pipeline looking for forbidden operators.
            var found = FindForbidden(doc.RootElement);
            if (found is not null)
                return $"Pipeline uses forbidden operator '{found}'.";
        }

        return null; // safe
    }

    private static string? FindForbidden(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (Forbidden.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                        return prop.Name;
                    var nested = FindForbidden(prop.Value);
                    if (nested is not null) return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindForbidden(item);
                    if (nested is not null) return nested;
                }
                break;
        }
        return null;
    }
}
