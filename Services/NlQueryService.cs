using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using QueryPilot.Api.Models;

namespace QueryPilot.Api.Services;

/// <summary>
/// The pipeline: question -> LLM writes a Mongo aggregation -> QueryGuard validates
/// -> execute read-only -> LLM summarizes the rows in plain language.
/// </summary>
public class NlQueryService
{
    private readonly LlmClient _llm;
    private readonly QueryGuard _guard;
    private readonly IMongoDatabase _db;

    // The LLM needs to know the schema to write correct queries.
    // In a real product you would introspect this; here it's explicit and readable.
    private const string SchemaDescription = """
        Collections available:

        customers: { _id: ObjectId, name: string, city: string, segment: "Enterprise"|"SMB"|"Consumer", joined: ISODate }
        orders:    { _id: ObjectId, customerName: string, product: string, category: "Hardware"|"Software"|"Services",
                     amount: number (INR), status: "Completed"|"Pending"|"Cancelled", orderDate: ISODate }
        """;

    public NlQueryService(LlmClient llm, QueryGuard guard, IMongoClient mongo, IConfiguration config)
    {
        _llm = llm;
        _guard = guard;
        _db = mongo.GetDatabase(config["Mongo:Database"] ?? "querypilot");
    }

    public async Task<AskResponse> AskAsync(string question, CancellationToken ct = default)
    {
        // ---- 1. LLM generates the query (JSON mode = structured output) ----
        var systemPrompt =
            "You translate natural-language questions into MongoDB aggregation pipelines. " +
            "Respond ONLY with a JSON object: {\"collection\": \"<name>\", \"pipeline\": [ ...stages... ]}. " +
            "Rules: read-only queries only; never use $out, $merge, $function, $where; " +
            "dates are ISODate - compare with {\"$gte\": {\"$date\": \"2026-01-01T00:00:00Z\"}} syntax; " +
            "prefer $group/$sort/$limit for 'top N' questions.\n\n" + SchemaDescription;

        var generated = await _llm.CompleteAsync(systemPrompt, question, jsonMode: true, ct);

        string collection;
        string pipelineJson;
        try
        {
            using var doc = JsonDocument.Parse(generated);
            collection = doc.RootElement.GetProperty("collection").GetString() ?? "";
            pipelineJson = doc.RootElement.GetProperty("pipeline").GetRawText();
        }
        catch (Exception)
        {
            return new AskResponse("The model produced an invalid query. Try rephrasing your question.",
                "", generated, new(), true, "Model output was not valid JSON.");
        }

        // ---- 2. Validate before touching the database ----
        var blockReason = _guard.Validate(collection, pipelineJson);
        if (blockReason is not null)
            return new AskResponse($"Query blocked for safety: {blockReason}",
                collection, pipelineJson, new(), true, blockReason);

        // ---- 3. Execute with a hard result cap ----
        var stages = BsonSerializer_DeserializePipeline(pipelineJson);
        stages.Add(new BsonDocument("$limit", _guard.MaxResults));

        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);
        var results = await _db.GetCollection<BsonDocument>(collection)
            .Aggregate(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        var rows = results.Select(ToPlainDictionary).ToList();

        // ---- 4. LLM summarizes the raw rows for the user ----
        var rowsJson = JsonSerializer.Serialize(rows.Take(20));
        var answer = await _llm.CompleteAsync(
            "You summarize database query results in 1-3 clear sentences. Use Indian Rupee (Rs.) for amounts. " +
            "Do not invent numbers not present in the data.",
            $"Question: {question}\nResults (JSON): {rowsJson}", jsonMode: false, ct);

        return new AskResponse(answer, collection, pipelineJson, rows, false, null);
    }

    private static List<BsonDocument> BsonSerializer_DeserializePipeline(string pipelineJson)
    {
        var array = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonArray>(pipelineJson);
        return array.Select(v => v.AsBsonDocument).ToList();
    }

    /// <summary>BsonDocument -> plain dictionary the JSON serializer (and React) understands.</summary>
    private static Dictionary<string, object?> ToPlainDictionary(BsonDocument doc)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var el in doc.Elements)
        {
            dict[el.Name] = el.Value.BsonType switch
            {
                BsonType.ObjectId => el.Value.AsObjectId.ToString(),
                BsonType.DateTime => el.Value.ToUniversalTime().ToString("yyyy-MM-dd"),
                BsonType.Int32 => el.Value.AsInt32,
                BsonType.Int64 => el.Value.AsInt64,
                BsonType.Double => el.Value.AsDouble,
                BsonType.Decimal128 => (object)el.Value.AsDecimal,
                BsonType.Boolean => el.Value.AsBoolean,
                BsonType.Null => null,
                BsonType.Document => ToPlainDictionary(el.Value.AsBsonDocument),
                _ => el.Value.ToString(),
            };
        }
        return dict;
    }
}
