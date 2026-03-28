using System.Text.Json.Serialization;

namespace AgenticWorkflow.Shared.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionStatus
{
    Created,
    AgentsRunning,
    Evaluating,
    AwaitingInput,
    AwaitingApproval,
    Accepted,
    Declined,
    Restarted,
    Failed
}

public sealed class SessionState
{
    public required string Id { get; set; }
    public required string Prompt { get; set; }
    public string? SystemPrompt { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Created;
    public List<AgentResult> AgentResults { get; set; } = [];
    public EvaluationResult? Evaluation { get; set; }
    public List<UserQuestion> PendingQuestions { get; set; } = [];
    public List<UserQuestionAnswer> AnsweredQuestions { get; set; } = [];
    public List<ChatMessage> ChatHistory { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record ChatMessage
{
    public required string Role { get; init; } // "user", "agent", "evaluator", "system", "question", "answer"
    public required string Content { get; init; }
    public string? MessageId { get; init; }
    public string? ParentMessageId { get; init; }
    public string? AgentName { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
