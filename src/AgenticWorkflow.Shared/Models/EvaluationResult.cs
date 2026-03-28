namespace AgenticWorkflow.Shared.Models;

public sealed record EvaluationResult
{
    public required string Winner { get; init; }
    public required string Reasoning { get; init; }
    public required Dictionary<string, AgentScore> Scores { get; init; }
    public required List<AgentResult> AllResults { get; init; }
}

public sealed record AgentScore
{
    public int Accuracy { get; init; }
    public int Completeness { get; init; }
    public int Clarity { get; init; }
    public int Relevance { get; init; }
    public int Total => Accuracy + Completeness + Clarity + Relevance;
}
