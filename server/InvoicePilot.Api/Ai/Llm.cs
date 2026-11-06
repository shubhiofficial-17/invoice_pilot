namespace InvoicePilot.Api.Ai;

public sealed record LlmContentPart(string Type, string? Text = null, string? ImageUrl = null)
{
    public static LlmContentPart FromText(string text) => new("text", Text: text);

    /// <summary>An image as a data URL (data:image/png;base64,...).</summary>
    public static LlmContentPart FromImage(string dataUrl) => new("image_url", ImageUrl: dataUrl);
}

/// <summary>A function the model may call. Parameters is a JSON Schema object.</summary>
public sealed record LlmTool(string Name, string Description, System.Text.Json.Nodes.JsonObject Parameters);

/// <summary>A call the model asked for. Arguments is the raw JSON string the model wrote.</summary>
public sealed record LlmToolCall(string Id, string Name, string Arguments);

public sealed record LlmMessage(
    string Role,
    IReadOnlyList<LlmContentPart> Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    /// <summary>The model's turn when it asked for tools.</summary>
    public static LlmMessage AssistantCalls(string? text, IReadOnlyList<LlmToolCall> calls) =>
        new("assistant", string.IsNullOrEmpty(text) ? [] : [LlmContentPart.FromText(text)], calls);

    /// <summary>A tool's result, answering one call.</summary>
    public static LlmMessage ToolResult(string toolCallId, string content) =>
        new("tool", [LlmContentPart.FromText(content)], ToolCallId: toolCallId);

    public static LlmMessage System(string text) => new("system", [LlmContentPart.FromText(text)]);

    public static LlmMessage User(string text) => new("user", [LlmContentPart.FromText(text)]);

    public static LlmMessage User(params LlmContentPart[] parts) => new("user", parts);

    public static LlmMessage Assistant(string text) => new("assistant", [LlmContentPart.FromText(text)]);

    /// <summary>All text parts joined, for logging and tests.</summary>
    public string Text => string.Join("\n", Content.Where(p => p.Text is not null).Select(p => p.Text));
}

public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    bool JsonMode = false,
    int MaxTokens = 2000,
    double Temperature = 0,
    IReadOnlyList<LlmTool>? Tools = null);

public sealed record LlmUsage(int PromptTokens, int CompletionTokens)
{
    public static readonly LlmUsage None = new(0, 0);

    public LlmUsage Add(LlmUsage other) => new(PromptTokens + other.PromptTokens, CompletionTokens + other.CompletionTokens);
}

public sealed record LlmResponse(string Content, string Model, LlmUsage Usage, IReadOnlyList<LlmToolCall>? ToolCalls = null)
{
    public IReadOnlyList<LlmToolCall> Calls => ToolCalls ?? [];
}

public interface ILlmClient
{
    bool Enabled { get; }

    string Model { get; }

    /// <exception cref="LlmUnavailableException">The model could not be reached or returned an error.</exception>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}

public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class AiOptions
{
    /// <summary>OpenAI-compatible base URL, normally the LLM Gateway, e.g. http://gateway:8080/v1/. Empty turns AI off.</summary>
    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public string Model { get; set; } = "gpt-4o-mini";

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Used when no AI endpoint is configured.</summary>
public sealed class DisabledLlmClient : ILlmClient
{
    public bool Enabled => false;

    public string Model => "none";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
        throw new LlmUnavailableException("AI is not configured (set Ai:BaseUrl).");
}
