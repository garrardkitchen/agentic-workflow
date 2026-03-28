namespace AgenticWorkflow.Shared.Models;

public sealed record AgentResult
{
    public required string AgentName { get; init; }
    public required string Model { get; init; }
    public required string ResponseText { get; init; }
    public long ElapsedMs { get; init; }
    public bool Failed { get; init; }
    public string? Error { get; init; }
}
