using System.Text.Json;

namespace QueryPilot.Api.Services;

/// <summary>
/// SECURITY LAYER.
///
/// The LLM writes MongoDB aggregation pipelines from user text, so its output is
/// UNTRUSTED. Every collection in the database is queryable by default - there is no
/// hand-written whitelist any more - but the guard still enforces:
///   1. No stages/operators that WRITE, DELETE, or EXECUTE CODE.
///   2. No access to collections listed in Query:DeniedCollections, including via
///      $lookup / $unionWith / $graphLookup, which can reach a second collection.
///   3. A $limit, applied by NlQueryService, so a broad question can't pull everything.
///
/// Checking that a collection EXISTS is a helpfulness feature (catch a hallucinated
/// name early, with a useful message) rather than a restriction.
/// </summary>
public class QueryGuard
{
    private readonly SchemaProvider _schema;
    private readonly int _maxResults;

    // Stages/operators that can write, delete, or execute arbitrary code - never allowed.
    // NOTE: read-only stages like $lookup, $unionWith, $facet ARE allowed, so joins and
    // multi-part questions work; their target collection is checked separately below.
    private static readonly string[] Forbidden =
    {
        "$out",              // writes results into a collection (can overwrite it)
        "$merge",            // upserts results into a collection
        "$function",         // runs server-side JavaScript
        "$where",            // runs server-side JavaScript
        "$accumulator",      // runs server-side JavaScript
        "$changeStream",     // tailable cursor, not a finite query
        "$currentOp",        // server internals
        "$listSessions",
        "$listLocalSessions",
        "$listSearchIndexes",
        "$listSampledQueries",
        "$planCacheStats",
        "$collStats",
        "$indexStats",
    };

    // Stage -> the property naming the OTHER collection it reads from.
    private static readonly Dictionary<string, string> CrossCollectionStages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["$lookup"] = "from",
            ["$graphLookup"] = "from",
            ["$unionWith"] = "coll",
        };

    public QueryGuard(SchemaProvider schema, IConfiguration config)
    {
        _schema = schema;
        _maxResults = config.GetValue("Query:MaxResults", 50);
    }

    public int MaxResults => _maxResults;

    /// <summary>
    /// Returns null if the query is safe to run, otherwise a human-readable reason.
    /// Also returns the correctly-cased collection name when the LLM got the casing wrong.
    /// </summary>
    public async Task<GuardResult> ValidateAsync(string collection, string pipelineJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collection))
            return GuardResult.Blocked("The model did not say which collection to query.");

        if (_schema.DeniedCollections.Contains(collection))
            return GuardResult.Blocked($"Collection '{collection}' is not available through this API.");

        // ---- Collection must exist. Not a whitelist - this is the live database. ----
        var schema = await _schema.GetAsync(ct);
        var actual = Find(schema, collection);

        if (actual is null)
        {
            // The collection may have been created after the cache was built. Refresh at
            // most once every 30s so a hallucinated name can't trigger a rebuild storm.
            _schema.InvalidateIfOlderThan(TimeSpan.FromSeconds(30));
            schema = await _schema.GetAsync(ct);
            actual = Find(schema, collection);
        }

        if (actual is null)
            return GuardResult.Blocked(
                $"There is no collection named '{collection}' in this database. " +
                $"Available collections: {string.Join(", ", schema.Collections.Select(c => c.Name))}.");

        // ---- Pipeline must be a JSON array of stages. ----
        JsonDocument doc;
        try { doc = JsonDocument.Parse(pipelineJson); }
        catch (JsonException) { return GuardResult.Blocked("Generated pipeline is not valid JSON."); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return GuardResult.Blocked("Pipeline must be a JSON array of aggregation stages.");

            var problem = Inspect(doc.RootElement);
            if (problem is not null)
                return GuardResult.Blocked(problem);
        }

        return GuardResult.Ok(actual.Name); // canonical casing
    }

    private static CollectionSchema? Find(DatabaseSchema schema, string name) =>
        schema.Collections.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Walks every stage looking for forbidden operators and denied join targets.</summary>
    private string? Inspect(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (Forbidden.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                        return $"Pipeline uses '{prop.Name}', which can modify the database or execute code.";

                    // $lookup/$unionWith/$graphLookup read a SECOND collection, so the
                    // deny list has to be enforced on their target too.
                    if (CrossCollectionStages.TryGetValue(prop.Name, out var targetProp) &&
                        prop.Value.ValueKind == JsonValueKind.Object &&
                        prop.Value.TryGetProperty(targetProp, out var target) &&
                        target.ValueKind == JsonValueKind.String)
                    {
                        var name = target.GetString();
                        if (name is not null && _schema.DeniedCollections.Contains(name))
                            return $"Pipeline uses '{prop.Name}' to read '{name}', " +
                                   "which is not available through this API.";
                    }

                    var nested = Inspect(prop.Value);
                    if (nested is not null) return nested;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = Inspect(item);
                    if (nested is not null) return nested;
                }
                break;
        }
        return null;
    }
}

public record GuardResult(bool IsAllowed, string? Reason, string Collection)
{
    public static GuardResult Ok(string collection) => new(true, null, collection);
    public static GuardResult Blocked(string reason) => new(false, reason, string.Empty);
}
