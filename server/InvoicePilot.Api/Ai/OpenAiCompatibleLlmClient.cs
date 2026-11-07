using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace InvoicePilot.Api.Ai;

/// <summary>
/// Calls POST {BaseUrl}chat/completions. Retries, provider fallback, caching and budgets are
/// the LLM Gateway's job, so this client makes one attempt and reports failures clearly.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public OpenAiCompatibleLlmClient(HttpClient http, IOptions<AiOptions> options)
    {
        _options = options.Value;
        _http = http;
        var baseUrl = _options.BaseUrl!.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/";
        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public bool Enabled => true;

    public string Model => _options.Model;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var body = BuildBody(request, _options.Model);
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("chat/completions", content, ct);
        }
        catch (HttpRequestException e)
        {
            throw new LlmUnavailableException("The AI service could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException("The AI service timed out.", e);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmUnavailableException($"The AI service returned {(int)response.StatusCode}.");
            }
            return ParseResponse(text, _options.Model);
        }
    }

    public static JsonObject BuildBody(LlmRequest request, string model)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            JsonNode contentNode;
            if (m.Content.Count == 1 && m.Content[0].Type == "text")
            {
                contentNode = JsonValue.Create(m.Content[0].Text ?? "");
            }
            else
            {
                var parts = new JsonArray();
                foreach (var p in m.Content)
                {
                    parts.Add(p.Type == "image_url"
                        ? new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = p.ImageUrl } }
                        : new JsonObject { ["type"] = "text", ["text"] = p.Text });
                }
                contentNode = parts;
            }
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = contentNode });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
        };
        if (request.JsonMode)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        return body;
    }

    public static LlmResponse ParseResponse(string json, string fallbackModel)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var message = root.GetProperty("choices")[0].GetProperty("message");
            var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : fallbackModel;
            var usage = LlmUsage.None;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = new LlmUsage(
                    u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var p) ? p : 0,
                    u.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt32(out var k) ? k : 0);
            }
            return new LlmResponse(text, model, usage);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new LlmUnavailableException("The AI service returned an unexpected response.", e);
        }
    }
}
