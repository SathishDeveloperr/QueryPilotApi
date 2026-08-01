using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using QueryPilot.Api.Models;

namespace QueryPilot.Api.Services;

/// <summary>
/// The pipeline: question -> LLM writes a Mongo aggregation -> QueryGuard validates
/// -> execute read-only -> LLM summarizes the rows in plain language.
///
/// The schema handed to the LLM is introspected from the live database by
/// SchemaProvider, so the model can only choose collections that really exist.
/// </summary>
public class NlQueryService
{
    private readonly LlmClient _llm;
    private readonly QueryGuard _guard;
    private readonly SchemaProvider _schema;
    private readonly IMongoDatabase _db;
    private readonly ILogger<NlQueryService> _log;

    public NlQueryService(LlmClient llm, QueryGuard guard, SchemaProvider schema,
        IMongoClient mongo, IConfiguration config, ILogger<NlQueryService> log)
    {
        _llm = llm;
        _guard = guard;
        _schema = schema;
        _db = mongo.GetDatabase(config["Mongo:Database"] ?? "querypilot");
        _log = log;
    }

    public async Task<AskResponse> AskAsync(string question, CancellationToken ct = default)
    {
        var schema = await _schema.GetAsync(ct);

        var systemPrompt =
            "You translate natural-language questions into MongoDB aggregation pipelines.\n" +
            "Respond ONLY with a JSON object: {\"collection\": \"<name>\", \"pipeline\": [ ...stages... ]}.\n" +
            "Rules:\n" +
            "- Read-only queries only. Never use $out, $merge, $function, $where or $accumulator.\n" +
            "- \"collection\" MUST be copied exactly from the schema below. Never invent a name.\n" +
            "- If the question is about an entity that is not in the schema, pick the closest " +
            "collection that exists (for example a question about \"customers\" should use the " +
            "collection that stores people, such as \"users\").\n" +
            "- Dates are ISODate - compare using {\"$gte\": {\"$date\": \"2026-01-01T00:00:00Z\"}}.\n" +
            "- Prefer $match/$group/$sort/$limit. $lookup and $unionWith are allowed for joins.\n" +
            "- Field names are case-sensitive; copy them exactly from the schema.\n\n" +
            schema.ToPromptText();

        // ---- 1. LLM generates the query, with one repair attempt ----
        string collection = "";
        string pipelineJson = "";
        string generated = "";
        GuardResult? guard = null;
        string? parseError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var userPrompt = attempt == 0
                ? question
                : $"{question}\n\nYour previous answer was rejected: {guard?.Reason ?? parseError}\n" +
                  "Return corrected JSON using only a collection name from the schema.";

            generated = await _llm.CompleteAsync(systemPrompt, userPrompt, jsonMode: true, ct);

            try
            {
                using var doc = JsonDocument.Parse(generated);
                collection = doc.RootElement.GetProperty("collection").GetString() ?? "";
                pipelineJson = doc.RootElement.GetProperty("pipeline").GetRawText();
                parseError = null;
            }
            catch (Exception)
            {
                parseError = "Model output was not valid JSON in the required shape.";
                guard = null;
                continue;
            }

            guard = await _guard.ValidateAsync(collection, pipelineJson, ct);
            if (guard.IsAllowed) break;

            _log.LogInformation("Attempt {Attempt} rejected: {Reason}", attempt + 1, guard.Reason);
        }

        if (parseError is not null)
            return new AskResponse("The model produced an invalid query. Try rephrasing your question.",
                collection, generated, new(), true, parseError);

        if (guard is null || !guard.IsAllowed)
            return new AskResponse($"Query blocked for safety: {guard?.Reason}",
                collection, pipelineJson, new(), true, guard?.Reason);

        collection = guard.Collection; // canonical casing from the database

        // ---- 2. Execute with a hard result cap ----
        List<BsonDocument> results;
        try
        {
            var stages = DeserializePipeline(pipelineJson);
            stages.Add(new BsonDocument("$limit", _guard.MaxResults));

            var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);
            results = await _db.GetCollection<BsonDocument>(collection)
                .Aggregate(pipeline, cancellationToken: ct)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // Log the detail server-side; never return raw driver/server text to the browser.
            _log.LogWarning(ex, "Aggregation failed on {Collection}", collection);
            return new AskResponse(
                "The generated query could not run against the database. Try rephrasing your question.",
                collection, pipelineJson, new(), true, "query_execution_failed");
        }

        var rows = results.Select(ToPlainDictionary).ToList();

        // ---- 3. LLM summarizes the raw rows for the user ----
        var rowsJson = JsonSerializer.Serialize(rows.Take(20));
        var answer = await _llm.CompleteAsync(
            "You summarize database query results in 1-3 clear sentences. Use Indian Rupee (Rs.) for amounts. " +
            "Do not invent numbers not present in the data. " +
            "If the result set is empty, say so plainly and suggest what else the user could ask.",
            $"Question: {question}\nCollection: {collection}\nResults (JSON): {rowsJson}",
            jsonMode: false, ct);

        return new AskResponse(answer, collection, pipelineJson, rows, false, null);
    }

    private static List<BsonDocument> DeserializePipeline(string pipelineJson)
    {
        var array = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonArray>(pipelineJson);
        return array.Select(v => v.AsBsonDocument).ToList();
    }

    /// <summary>BsonDocument -> plain dictionary the JSON serializer (and React) understands.</summary>
    private static Dictionary<string, object?> ToPlainDictionary(BsonDocument doc)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var el in doc.Elements)
            dict[el.Name] = ToPlainValue(el.Value);
        return dict;
    }

    private static object? ToPlainValue(BsonValue value) => value.BsonType switch
    {
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.DateTime => value.ToUniversalTime().ToString("yyyy-MM-dd"),
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        // NaN / Infinity (from $avg or $divide) cannot be written as JSON - null them out
        // rather than letting the response serializer throw.
        BsonType.Double => double.IsFinite(value.AsDouble) ? (object?)value.AsDouble : null,
        BsonType.Decimal128 => ToDecimalOrNull(value),
        BsonType.Boolean => value.AsBoolean,
        BsonType.Null or BsonType.Undefined => null,
        BsonType.Document => ToPlainDictionary(value.AsBsonDocument),
        BsonType.Array => value.AsBsonArray.Select(ToPlainValue).ToList(),
        _ => value.ToString(),
    };

    /// <summary>Decimal128 can hold NaN/Infinity, which overflow System.Decimal.</summary>
    private static object? ToDecimalOrNull(BsonValue value)
    {
        try { return value.AsDecimal; }
        catch (OverflowException) { return null; }
    }
}
