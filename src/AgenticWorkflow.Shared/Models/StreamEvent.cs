namespace AgenticWorkflow.Shared.Models;

public sealed record StreamEvent
{
    public required string Type { get; init; } // "status", "agent_token", "agent_complete", "evaluation", "question_required", "error"
    public required string SessionId { get; init; }
    public string? AgentName { get; init; }
    public string? MessageId { get; init; }
    public bool IsContinuation { get; init; }
    public string? Content { get; init; }
    public SessionStatus? Status { get; init; }
    public AgentResult? AgentResult { get; init; }
    public EvaluationResult? Evaluation { get; init; }
    public UserQuestion? Question { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
