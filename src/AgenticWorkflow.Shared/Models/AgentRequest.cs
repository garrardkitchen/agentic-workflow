namespace AgenticWorkflow.Shared.Models;

public sealed record AgentRequest
{
    public required string Prompt { get; init; }
    public required string SessionId { get; init; }
    public string? SystemPromptOverride { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
