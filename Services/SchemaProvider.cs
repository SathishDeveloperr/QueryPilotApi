using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace QueryPilot.Api.Services;

/// <summary>
/// Reads the REAL shape of the database at runtime instead of relying on a hardcoded
/// description. Two jobs:
///   1. Give the LLM an accurate list of collections + fields, so it can never invent
///      a collection that does not exist (that was the "customers" bug).
///   2. Give QueryGuard the set of collections that actually exist, which replaces the
///      old hand-maintained Query:AllowedCollections whitelist.
///
/// The result is cached and refreshes after Query:SchemaCacheMinutes.
/// </summary>
public class SchemaProvider
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<SchemaProvider> _log;
    private readonly int _sampleSize;
    private readonly TimeSpan _cacheFor;
    private readonly HashSet<string> _denied;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private DatabaseSchema? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    // Collections that are noise for a question-answering tool.
    private static readonly string[] IgnoredPrefixes = { "system.", "__" };

    // A string field with more distinct values than this is free text, not an enum.
    private const int MaxEnumValues = 8;

    // Never list example values for a field whose name looks like a secret or a personal
    // identifier - those examples are sent verbatim to the LLM provider in the prompt.
    private static readonly Regex SensitiveField = new(
        @"otp|token|password|passwd|pwd|hash|salt|secret|apikey|api_key|email|phone|mobile|address|card|cvv|pin\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Enum detection is only meaningful with a reasonable sample.
    private const int MinDocsForEnum = 10;

    public SchemaProvider(IMongoClient mongo, IConfiguration config, ILogger<SchemaProvider> log)
    {
        _db = mongo.GetDatabase(config["Mongo:Database"] ?? "querypilot");
        _log = log;
        _sampleSize = config.GetValue("Query:SchemaSampleSize", 25);
        _cacheFor = TimeSpan.FromMinutes(config.GetValue("Query:SchemaCacheMinutes", 10));
        _denied = config.GetSection("Query:DeniedCollections").Get<string[]>()
            ?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Collections the API refuses to touch. Empty by default.</summary>
    public IReadOnlySet<string> DeniedCollections => _denied;

    public async Task<DatabaseSchema> GetAsync(CancellationToken ct = default)
    {
        if (IsFresh) return _cached!;

        await _lock.WaitAsync(ct);
        try
        {
            if (IsFresh) return _cached!;

            _cached = await BuildAsync(ct);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool IsFresh => _cached is not null && DateTimeOffset.UtcNow - _cachedAt < _cacheFor;

    /// <summary>
    /// Forces a refresh only if the cache is already older than <paramref name="age"/>.
    /// Used when the LLM names an unknown collection: without the floor, one bad guess
    /// would drop the cache for every user and re-introspect the whole database.
    /// </summary>
    public void InvalidateIfOlderThan(TimeSpan age)
    {
        if (DateTimeOffset.UtcNow - _cachedAt > age)
            _cachedAt = DateTimeOffset.MinValue;
    }

    private async Task<DatabaseSchema> BuildAsync(CancellationToken ct)
    {
        var dbName = _db.DatabaseNamespace.DatabaseName;
        List<string> names;

        try
        {
            names = new List<string>();
            using var cursor = await _db.ListCollectionNamesAsync(cancellationToken: ct);
            while (await cursor.MoveNextAsync(ct))
                names.AddRange(cursor.Current);
        }
        catch (Exception ex)
        {
            // Unreachable cluster or missing listCollections privilege must not turn every
            // /ask request into an unhandled 500. ToPromptText handles the empty case.
            _log.LogError(ex, "Could not list collections in {Database}", dbName);
            return new DatabaseSchema(dbName, new List<CollectionSchema>());
        }

        names = names
            .Where(n => !IgnoredPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Where(n => !_denied.Contains(n)) // denied collections never reach the prompt
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var collections = new List<CollectionSchema>();

        foreach (var name in names)
        {
            try
            {
                var docs = await _db.GetCollection<BsonDocument>(name)
                    .Find(FilterDefinition<BsonDocument>.Empty)
                    .Limit(_sampleSize)
                    .ToListAsync(ct);

                collections.Add(new CollectionSchema(name, InferFields(docs), docs.Count));
            }
            catch (Exception ex)
            {
                // A single unreadable collection must not break the whole schema.
                _log.LogWarning(ex, "Could not sample collection {Collection}", name);
                collections.Add(new CollectionSchema(name, new List<FieldSchema>(), 0));
            }
        }

        _log.LogInformation("Schema introspected: {Count} collections ({Names})",
            collections.Count, string.Join(", ", collections.Select(c => c.Name)));

        return new DatabaseSchema(dbName, collections);
    }

    /// <summary>
    /// Walks the sampled documents and records every field it sees, one level into
    /// nested objects and arrays-of-objects. For low-cardinality, non-sensitive strings
    /// it also records the observed values, which helps the LLM write correct $match
    /// filters (e.g. status is one of "Completed", "Pending").
    /// </summary>
    private static List<FieldSchema> InferFields(IReadOnlyList<BsonDocument> docs)
    {
        var acc = new FieldAccumulator();

        foreach (var doc in docs)
            Walk(doc, prefix: "", acc, depth: 0);

        return acc.Types
            .OrderByDescending(kv => acc.Occurrences.GetValueOrDefault(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var path = kv.Key;
                List<string>? enumValues = null;

                if (docs.Count >= MinDocsForEnum &&
                    !acc.FreeText.Contains(path) &&
                    acc.Values.TryGetValue(path, out var distinct) &&
                    distinct.Count is > 0 and <= MaxEnumValues)
                {
                    enumValues = distinct.OrderBy(v => v, StringComparer.Ordinal).ToList();
                }

                // Occurrences for array-element paths are counted per element, not per
                // document, so that count can't be compared against docs.Count.
                var optional = path.Contains("[]", StringComparison.Ordinal)
                    || acc.Occurrences.GetValueOrDefault(path) < docs.Count;

                return new FieldSchema(
                    Name: path,
                    Type: string.Join("|", kv.Value.OrderBy(t => t, StringComparer.Ordinal)),
                    Values: enumValues,
                    Optional: optional);
            })
            .ToList();
    }

    private static void Walk(BsonDocument doc, string prefix, FieldAccumulator acc, int depth)
    {
        foreach (var el in doc.Elements)
        {
            var path = prefix.Length == 0 ? el.Name : $"{prefix}.{el.Name}";

            acc.Type(path).Add(FriendlyType(el.Value));
            acc.Occurrences[path] = acc.Occurrences.GetValueOrDefault(path) + 1;

            if (el.Value.BsonType == BsonType.String)
            {
                var s = el.Value.AsString;

                if (SensitiveField.IsMatch(el.Name) || s.Length > 40)
                {
                    // Secrets, personal identifiers and long free text are never sampled
                    // as example values - the prompt goes to a third-party LLM provider.
                    acc.FreeText.Add(path);
                }
                else
                {
                    var vals = acc.Value(path);
                    if (vals.Count >= MaxEnumValues) acc.FreeText.Add(path);
                    else vals.Add(s);
                }
            }

            if (depth >= 1) continue; // one level of nesting is enough for prompting

            if (el.Value.BsonType == BsonType.Document)
            {
                Walk(el.Value.AsBsonDocument, path, acc, depth + 1);
            }
            else if (el.Value.BsonType == BsonType.Array)
            {
                foreach (var item in el.Value.AsBsonArray.Take(3))
                    if (item.BsonType == BsonType.Document)
                        Walk(item.AsBsonDocument, path + "[]", acc, depth + 1);
            }
        }
    }

    private static string FriendlyType(BsonValue v) => v.BsonType switch
    {
        BsonType.ObjectId => "ObjectId",
        BsonType.String => "string",
        BsonType.Int32 or BsonType.Int64 => "int",
        BsonType.Double or BsonType.Decimal128 => "number",
        BsonType.Boolean => "bool",
        BsonType.DateTime => "ISODate",
        BsonType.Array => "array",
        BsonType.Document => "object",
        BsonType.Null => "null",
        _ => v.BsonType.ToString().ToLowerInvariant(),
    };

    /// <summary>Scratch state used while sampling one collection.</summary>
    private sealed class FieldAccumulator
    {
        public Dictionary<string, HashSet<string>> Types { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> Values { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Occurrences { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FreeText { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Type(string path)
        {
            if (!Types.TryGetValue(path, out var set))
                Types[path] = set = new HashSet<string>(StringComparer.Ordinal);
            return set;
        }

        public HashSet<string> Value(string path)
        {
            if (!Values.TryGetValue(path, out var set))
                Values[path] = set = new HashSet<string>(StringComparer.Ordinal);
            return set;
        }
    }
}

public record FieldSchema(string Name, string Type, List<string>? Values, bool Optional);

public record CollectionSchema(string Name, List<FieldSchema> Fields, int Sampled);

public record DatabaseSchema(string Database, List<CollectionSchema> Collections)
{
    /// <summary>Renders the schema as compact text for the LLM system prompt.</summary>
    public string ToPromptText()
    {
        if (Collections.Count == 0)
            return "The database is empty - there are no collections to query.";

        var sb = new StringBuilder();
        sb.AppendLine($"Database \"{Database}\" contains these collections. " +
                      "You may ONLY use a collection name from this list - never invent one:");
        sb.AppendLine();

        foreach (var c in Collections)
        {
            if (c.Fields.Count == 0)
            {
                sb.AppendLine($"{c.Name}: (empty collection)");
                continue;
            }

            var fields = c.Fields.Select(f =>
            {
                var text = $"{f.Name}: {f.Type}";
                if (f.Values is { Count: > 0 })
                    text += " one of [" + string.Join(", ", f.Values.Select(v => $"\"{v}\"")) + "]";
                if (f.Optional) text += " (sometimes missing)";
                return text;
            });

            sb.AppendLine($"{c.Name}: {{ {string.Join(", ", fields)} }}");
        }

        return sb.ToString().TrimEnd();
    }
}
