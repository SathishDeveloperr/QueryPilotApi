namespace QueryPilot.Api.Models;

public record AskRequest(string Question);

/// <summary>What the LLM must return: which collection to query and an aggregation pipeline.</summary>
public record GeneratedQuery(string Collection, string PipelineJson);

public record AskResponse(
    string Answer,           // plain-language summary of the results
    string Collection,       // which collection was queried
    string GeneratedPipeline,// the Mongo aggregation the LLM wrote (shown in the UI for transparency)
    List<Dictionary<string, object?>> Rows, // raw results for the table
    bool WasBlocked,         // true if validation rejected the query
    string? BlockReason);
