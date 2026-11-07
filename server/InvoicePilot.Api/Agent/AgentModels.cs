using System.Text.Json;
using System.Text.Json.Serialization;
using InvoicePilot.Api.Ai;

namespace InvoicePilot.Api.Agent;

public enum Outcome { Approve, RouteForApproval, Hold, Reject }

public sealed record SupplierEmail(string To, string Subject, string Body);

/// <summary>What the agent recommends. It is only a proposal: policy checks and a person come next.</summary>
public sealed record AgentProposal(
    Outcome Outcome,
    string Reason,
    IReadOnlyList<string> FindingCodes,
    SupplierEmail? SupplierEmail);

public enum StepKind { Model, Tool }

/// <summary>One entry in the agent's trace: a model call or a tool call.</summary>
public sealed record AgentStep(
    int Index,
    StepKind Kind,
    string? ToolName,
    string? Arguments,
    string Output,
    bool IsError,
    int PromptTokens,
    int CompletionTokens,
    long DurationMs,
    DateTimeOffset At);

public enum AgentRunStatus { Completed, Failed }

public sealed record AgentRun(
    string Model,
    AgentRunStatus Status,
    AgentProposal? Proposal,
    string? FailureReason,
    IReadOnlyList<AgentStep> Steps,
    LlmUsage Usage,
    int ModelCalls,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

public sealed class AgentOptions
{
    public int MaxModelCalls { get; set; } = 12;

    public int MaxToolCallsPerTurn { get; set; } = 6;

    public int TokenBudget { get; set; } = 60_000;

    public int MaxToolOutputChars { get; set; } = 8_000;

    /// <summary>Waits between retries when the model is unavailable.</summary>
    public int[] RetryDelaysMs { get; set; } = [1_000, 3_000];
}

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
