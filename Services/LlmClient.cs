using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace QueryPilot.Api.Services;

/// <summary>
/// Minimal OpenAI-compatible chat client (Groq / OpenAI / OpenRouter).
/// Same pattern as the RAG chatbot project - one POST to /chat/completions.
/// </summary>
public class LlmClient
{
    private readonly HttpClient _http;
    private readonly string _model;

    public LlmClient(HttpClient http, IConfiguration config)
    {
        _http = http;

        var baseUrl = Value(config["Llm:BaseUrl"]) ?? "https://api.groq.com/openai/v1";
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

        // Resolution order:
        //   1. Llm:ApiKey        -> appsettings.Development.json (gitignored) or user-secrets
        //   2. Llm__ApiKey       -> environment variable (double underscore = ':' in .NET config)
        //   3. GROQ_API_KEY      -> plain environment variable, handy for Docker / CI
        // NEVER put the real key in appsettings.json: that file is committed to git, and
        // Groq/OpenAI automatically revoke any key they detect in a public repo -> HTTP 401.
        var apiKey = Value(config["Llm:ApiKey"])
                     ?? Value(Environment.GetEnvironmentVariable("Llm__ApiKey"))
                     ?? Value(Environment.GetEnvironmentVariable("GROQ_API_KEY"));

        if (apiKey is null)
            throw new InvalidOperationException(
                "No LLM API key configured. Set \"Llm:ApiKey\" in appsettings.Development.json " +
                "(gitignored) or set the GROQ_API_KEY environment variable. " +
                "Get a key at https://console.groq.com/keys");

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        _model = Value(config["Llm:ChatModel"]) ?? "llama-3.3-70b-versatile";
    }

    // Treats null / "" / whitespace / leftover placeholders as "not configured".
    private static string? Value(string? raw)
    {
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.StartsWith("PASTE_", StringComparison.OrdinalIgnoreCase)) return null;
        if (v.StartsWith("<") && v.EndsWith(">")) return null;
        return v;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt,
        bool jsonMode = false, CancellationToken ct = default)
    {
        object payload = jsonMode
            ? new
            {
                model = _model,
                temperature = 0.1,
                // response_format json_object forces the model to emit valid JSON -
                // the building block of "structured outputs".
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
            }
            : new
            {
                model = _model,
                temperature = 0.3,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
            };

        using var resp = await _http.PostAsync("chat/completions",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);

        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "LLM API rejected the API key (401). The key is missing, mistyped, or has been revoked - " +
                "keys pushed to a public repository are revoked automatically. " +
                "Create a new key at https://console.groq.com/keys and put it in " +
                "appsettings.Development.json under \"Llm:ApiKey\" (or the GROQ_API_KEY env var). " +
                $"Provider response: {body}");

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM API error {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }
}
