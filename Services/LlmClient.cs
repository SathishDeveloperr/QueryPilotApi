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
        var baseUrl = (config["Llm:BaseUrl"] ?? "https://api.groq.com/openai/v1").TrimEnd('/');
        _http.BaseAddress = new Uri(baseUrl + "/");
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config["Llm:ApiKey"]);
        _model = config["Llm:ChatModel"] ?? "llama-3.3-70b-versatile";
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
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM API error {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }
}
